package com.melfegor.nice64

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.melfegor.nice64.api.BridgeControllerDiagnosticDevice
import com.melfegor.nice64.api.ControllerMappingEditorCoordinator
import com.melfegor.nice64.api.ControllerProfileUiRepository
import com.melfegor.nice64.api.ControllerProfileUiRepositoryException
import com.melfegor.nice64.api.ControllerProfileUiRepositorySnapshot
import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

class ControllerMappingEditorRecreationTestActivity : ComponentActivity() {
    internal val model: RecreationEditorViewModel by viewModels {
        object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>): T =
                RecreationEditorViewModel() as T
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            val state by model.editor.state.collectAsStateWithLifecycle()
            Column(Modifier.verticalScroll(rememberScrollState())) {
                ControllerMappingEditorScreen(
                    state,
                    MappingEditorActions(
                        onCancelCapture = model.editor::cancelCapture,
                        onApplyCandidate = model.editor::applyCandidate,
                        onCancelRestore = model.editor::cancelRestoreDefaults,
                        onConfirmRestore = model.editor::confirmRestoreDefaults,
                        onContinueEditing = model.editor::continueEditing,
                        onDiscard = model.editor::discardAndClose,
                    ),
                )
            }
        }
    }
}

internal class RecreationEditorViewModel : ViewModel() {
    val editor = ControllerMappingEditorCoordinator(RecreationEditorRepository)
    private var saveJob: Job? = null

    fun open(profile: ControllerProfileV1, device: BridgeControllerDiagnosticDevice) {
        viewModelScope.launch { editor.open(profile.profileId, device) }
    }

    fun save() {
        saveJob = viewModelScope.launch { editor.save() }
    }
}

internal object RecreationEditorRepository : ControllerProfileUiRepository {
    private lateinit var snapshot: ControllerProfileUiRepositorySnapshot
    var updateCount = 0
    var authoritativeReloadCount = 0
    var delayUpdate = false
    var releaseUpdate = CompletableDeferred<Unit>().apply { complete(Unit) }
    private var updateCommitted = false

    fun reset(profile: ControllerProfileV1) {
        snapshot = ControllerProfileUiRepositorySnapshot(listOf(profile), profile.profileId, profile.identity)
        updateCount = 0
        authoritativeReloadCount = 0
        delayUpdate = false
        releaseUpdate = CompletableDeferred<Unit>().apply { complete(Unit) }
        updateCommitted = false
    }

    override suspend fun load(): ControllerProfileUiRepositorySnapshot {
        if (updateCommitted) authoritativeReloadCount++
        return snapshot
    }

    override suspend fun create(profile: ControllerProfileV1) = Unit

    override suspend fun update(profile: ControllerProfileV1) {
        if (snapshot.profiles.none { it.profileId == profile.profileId }) {
            throw ControllerProfileUiRepositoryException("The controller profile no longer exists.")
        }
        updateCount++
        if (delayUpdate) {
            releaseUpdate = CompletableDeferred()
            releaseUpdate.await()
        }
        snapshot = snapshot.copy(profiles = listOf(profile))
        updateCommitted = true
    }

    override suspend fun select(profileId: String?) = Unit
    override suspend fun delete(profileId: String) = Unit
    override suspend fun selectController(identity: ControllerIdentityV1?) = Unit
}
