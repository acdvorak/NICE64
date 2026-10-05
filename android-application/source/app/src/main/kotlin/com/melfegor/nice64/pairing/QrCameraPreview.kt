package com.melfegor.nice64.pairing

import android.content.Context
import android.annotation.SuppressLint
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import java.util.concurrent.atomic.AtomicBoolean

internal class QrScanSession {
    private val delivered = AtomicBoolean(false)
    fun deliverOnce(rawValue: String?, deliver: (String) -> Unit): Boolean {
        val raw = rawValue?.takeIf(String::isNotBlank) ?: return false
        if (!delivered.compareAndSet(false, true)) return false
        deliver(raw)
        return true
    }
}

@Composable
internal fun QrCameraPreview(
    onQrCode: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val previewView = remember {
        PreviewView(context).apply {
            implementationMode = PreviewView.ImplementationMode.COMPATIBLE
            scaleType = PreviewView.ScaleType.FILL_CENTER
        }
    }
    val session = remember { QrScanSession() }

    AndroidView(factory = { previewView }, modifier = modifier)
    DisposableEffect(lifecycleOwner, previewView) {
        val binding = CameraBinding(context, lifecycleOwner, previewView, session, onQrCode)
        binding.start()
        onDispose { binding.close() }
    }
}

private class CameraBinding(
    private val context: Context,
    private val lifecycleOwner: LifecycleOwner,
    private val previewView: PreviewView,
    private val session: QrScanSession,
    private val onQrCode: (String) -> Unit,
) : AutoCloseable {
    private val active = AtomicBoolean(true)
    private val scanner = BarcodeScanning.getClient(
        BarcodeScannerOptions.Builder()
            .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
            .build(),
    )
    private var provider: ProcessCameraProvider? = null

    fun start() {
        val future = ProcessCameraProvider.getInstance(context)
        future.addListener({
            if (!active.get()) return@addListener
            runCatching { future.get() }.onSuccess { cameraProvider ->
                if (!active.get()) return@onSuccess
                provider = cameraProvider
                bind(cameraProvider)
            }
        }, ContextCompat.getMainExecutor(context))
    }

    @SuppressLint("UnsafeOptInUsageError")
    private fun bind(cameraProvider: ProcessCameraProvider) {
        val preview = Preview.Builder().build().also {
            it.surfaceProvider = previewView.surfaceProvider
        }
        val analysis = ImageAnalysis.Builder()
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .build()
        analysis.setAnalyzer(ContextCompat.getMainExecutor(context)) { imageProxy ->
            val mediaImage = imageProxy.image
            if (mediaImage == null || !active.get()) {
                imageProxy.close()
                return@setAnalyzer
            }
            val image = InputImage.fromMediaImage(mediaImage, imageProxy.imageInfo.rotationDegrees)
            scanner.process(image)
                .addOnSuccessListener { barcodes ->
                    if (active.get()) {
                        barcodes.firstOrNull { it.format == Barcode.FORMAT_QR_CODE }?.rawValue?.let {
                            session.deliverOnce(it, onQrCode)
                        }
                    }
                }
                .addOnCompleteListener { imageProxy.close() }
        }
        cameraProvider.unbindAll()
        cameraProvider.bindToLifecycle(lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
    }

    override fun close() {
        active.set(false)
        provider?.unbindAll()
        scanner.close()
    }
}
