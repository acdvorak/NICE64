package com.melfegor.nice64.api

import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import com.melfegor.nice64.core.input.AxisDirectionV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ControllerMappingEditorTest {
    @Test fun loadsAuthoritativeCustomWorkingCopyAndRejectsBuiltIn() = runTest {
        val custom = profile()
        val repository = FakeEditorRepository(custom)
        val editor = ControllerMappingEditorCoordinator(repository)

        editor.open(custom.profileId, device())
        assertEquals(custom, editor.state.value.original)
        assertEquals(custom, editor.state.value.working)
        assertFalse(editor.state.value.dirty)

        editor.open(StandardAndroidGamepadProfile.profile.profileId, device())
        assertFalse(editor.state.value.active)
        assertTrue(editor.state.value.errorMessage!!.contains("read-only"))
    }

    @Test fun buttonCaptureIgnoresRepeatWrongDeviceAndIneligibleSource() = runTest {
        val editor = openedEditor()
        assertTrue(editor.beginButtonCapture(N64SemanticTargetV1.A))
        assertTrue(editor.handleKey(key(deviceId = 8)))
        assertTrue(editor.state.value.captureActive)
        assertTrue(editor.handleKey(key(repeat = 1)))
        assertTrue(editor.state.value.captureActive)
        assertTrue(editor.handleKey(key(eligible = false)))
        assertTrue(editor.state.value.captureActive)

        assertTrue(editor.handleKey(key(keyCode = KeyEvent.KEYCODE_BUTTON_X)))
        assertFalse(editor.state.value.captureActive)
        assertTrue(editor.state.value.candidate is MappingCaptureCandidate.Button)
    }

    @Test fun keyUpIsConsumedButNeverCaptured() = runTest {
        val editor = openedEditor()
        editor.beginButtonCapture(N64SemanticTargetV1.B)
        editor.handleKey(key(down = false))
        assertTrue(editor.state.value.captureActive)
        assertNull(editor.state.value.candidate)
    }

    @Test fun axisNoiseIgnoredAndDeliberatePositiveAndNegativeDirectionsCaptured() = runTest {
        val editor = openedEditor()
        editor.beginAxisCapture(N64SemanticTargetV1.C_RIGHT)
        editor.handleAxes(axes(MotionEvent.AXIS_Z to 0.2f))
        assertTrue(editor.state.value.captureActive)
        editor.handleAxes(axes(MotionEvent.AXIS_Z to 0.9f))
        val positive = (editor.state.value.candidate as MappingCaptureCandidate.Axis).binding
        assertEquals(AxisDirectionV1.POSITIVE, positive.direction)
        editor.cancelCapture()

        editor.beginAxisCapture(N64SemanticTargetV1.C_LEFT)
        editor.handleAxes(axes(MotionEvent.AXIS_Z to -0.9f))
        val negative = (editor.state.value.candidate as MappingCaptureCandidate.Axis).binding
        assertEquals(AxisDirectionV1.NEGATIVE, negative.direction)
    }

    @Test fun analogAxisCaptureUsesBothAndYDefaultsToInverted() = runTest {
        val editor = openedEditor()
        editor.beginAxisCapture(N64SemanticTargetV1.ANALOG_Y)
        editor.handleAxes(axes(MotionEvent.AXIS_RY to 0.85f))
        val binding = (editor.state.value.candidate as MappingCaptureCandidate.Axis).binding
        assertEquals(AxisDirectionV1.BOTH, binding.direction)
        assertTrue(binding.inverted)
    }

    @Test fun analogPairCapturesDistinctAxesAndAppliesTogether() = runTest {
        val editor = openedEditor()
        editor.beginAnalogPairCapture()
        editor.handleAxes(axes(MotionEvent.AXIS_X to 0.9f))
        assertEquals(MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_Y, editor.state.value.phase)
        editor.handleAxes(axes(MotionEvent.AXIS_X to 1f, MotionEvent.AXIS_Y to -0.9f))
        val pair = editor.state.value.candidate as MappingCaptureCandidate.AnalogPair
        assertEquals(MotionEvent.AXIS_X, pair.xAxisCode)
        assertEquals(MotionEvent.AXIS_Y, pair.yAxisCode)
        editor.applyCandidate()
        assertEquals(
            setOf(N64SemanticTargetV1.ANALOG_X, N64SemanticTargetV1.ANALOG_Y),
            editor.state.value.working!!.axisBindings.filter { it.target.isAnalog }.map { it.target }.toSet(),
        )
    }

    @Test fun hatAndAnalogTriggerUseExistingDigitalAxisContract() = runTest {
        val editor = openedEditor()
        editor.beginAxisCapture(N64SemanticTargetV1.DPAD_LEFT)
        editor.handleAxes(axes(MotionEvent.AXIS_HAT_X to -1f))
        val hat = (editor.state.value.candidate as MappingCaptureCandidate.Axis).binding
        assertEquals(MotionEvent.AXIS_HAT_X, hat.axisCode)
        assertEquals(AxisDirectionV1.NEGATIVE, hat.direction)
        assertEquals(0.5f, hat.pressThreshold)
        editor.cancelCapture()

        editor.beginAxisCapture(N64SemanticTargetV1.Z)
        editor.handleAxes(axes(MotionEvent.AXIS_LTRIGGER to 0.8f))
        val trigger = (editor.state.value.candidate as MappingCaptureCandidate.Axis).binding
        assertEquals(MotionEvent.AXIS_LTRIGGER, trigger.axisCode)
        assertEquals(AxisDirectionV1.POSITIVE, trigger.direction)
    }

    @Test fun digitalTriggerKeyCapturesAsButton() = runTest {
        val editor = openedEditor()
        editor.beginButtonCapture(N64SemanticTargetV1.Z)
        editor.handleKey(key(keyCode = KeyEvent.KEYCODE_BUTTON_L2))
        assertEquals(
            KeyEvent.KEYCODE_BUTTON_L2,
            (editor.state.value.candidate as MappingCaptureCandidate.Button).keyCode,
        )
    }

    @Test fun allControllerDpadDirectionsBecomeButtonCaptureCandidates() = runTest {
        val expected = listOf(
            KeyEvent.KEYCODE_DPAD_UP,
            KeyEvent.KEYCODE_DPAD_DOWN,
            KeyEvent.KEYCODE_DPAD_LEFT,
            KeyEvent.KEYCODE_DPAD_RIGHT,
        )
        expected.forEach { keyCode ->
            val editor = openedEditor()
            assertTrue(editor.beginButtonCapture(N64SemanticTargetV1.DPAD_UP))
            assertTrue(editor.handleKey(key(keyCode = keyCode)))
            assertEquals(
                keyCode,
                (editor.state.value.candidate as MappingCaptureCandidate.Button).keyCode,
            )
        }
    }

    @Test fun allControllerDpadDirectionsSupportActionUpFallback() = runTest {
        val expected = listOf(
            KeyEvent.KEYCODE_DPAD_UP,
            KeyEvent.KEYCODE_DPAD_DOWN,
            KeyEvent.KEYCODE_DPAD_LEFT,
            KeyEvent.KEYCODE_DPAD_RIGHT,
        )
        expected.forEach { keyCode ->
            val editor = openedEditor()
            assertTrue(editor.beginButtonCapture(N64SemanticTargetV1.DPAD_UP))
            assertTrue(editor.handleKey(key(down = false, keyCode = keyCode)))
            assertEquals(
                keyCode,
                (editor.state.value.candidate as MappingCaptureCandidate.Button).keyCode,
            )
        }
    }

    @Test fun normalDpadDownWinsAndItsReleaseIsConsumedExactlyOnce() = runTest {
        val editor = openedEditor()
        editor.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        val down = key(keyCode = KeyEvent.KEYCODE_DPAD_UP)
        val up = key(down = false, keyCode = KeyEvent.KEYCODE_DPAD_UP)

        assertTrue(editor.handleKey(down))
        val candidate = editor.state.value.candidate
        assertTrue(editor.shouldPreDispatchKey(up))
        assertTrue(editor.handleKey(up))
        assertEquals(candidate, editor.state.value.candidate)
        assertFalse(editor.shouldPreDispatchKey(up))
        assertFalse(editor.handleKey(up))
    }

    @Test fun actionUpFallbackNeverAppliesToOrdinaryButtonsOrTriggers() = runTest {
        listOf(
            KeyEvent.KEYCODE_BUTTON_A,
            KeyEvent.KEYCODE_BUTTON_B,
            KeyEvent.KEYCODE_BUTTON_X,
            KeyEvent.KEYCODE_BUTTON_Y,
            KeyEvent.KEYCODE_BUTTON_L1,
            KeyEvent.KEYCODE_BUTTON_R1,
            KeyEvent.KEYCODE_BUTTON_L2,
            KeyEvent.KEYCODE_BUTTON_R2,
            KeyEvent.KEYCODE_BUTTON_START,
            KeyEvent.KEYCODE_BUTTON_SELECT,
            KeyEvent.KEYCODE_BUTTON_THUMBL,
            KeyEvent.KEYCODE_BUTTON_THUMBR,
        ).forEach { keyCode ->
            val editor = openedEditor()
            editor.beginButtonCapture(N64SemanticTargetV1.A)
            assertTrue(editor.handleKey(key(down = false, keyCode = keyCode)))
            assertTrue(editor.state.value.captureActive)
            assertNull(editor.state.value.candidate)
        }
    }

    @Test fun dpadUpFallbackRequiresSelectedEligibleController() = runTest {
        val wrongDevice = openedEditor()
        wrongDevice.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        val wrong = key(deviceId = 8, down = false, keyCode = KeyEvent.KEYCODE_DPAD_UP)
        assertFalse(wrongDevice.shouldPreDispatchKey(wrong))
        assertTrue(wrongDevice.handleKey(wrong))
        assertNull(wrongDevice.state.value.candidate)

        val ineligible = openedEditor()
        ineligible.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        val keyboardOnly = key(eligible = false, down = false, keyCode = KeyEvent.KEYCODE_DPAD_UP)
        assertFalse(ineligible.shouldPreDispatchKey(keyboardOnly))
        assertTrue(ineligible.handleKey(keyboardOnly))
        assertNull(ineligible.state.value.candidate)
    }

    @Test fun dpadUpFallbackIsButtonCaptureOnly() = runTest {
        val axis = openedEditor()
        axis.beginAxisCapture(N64SemanticTargetV1.C_UP)
        assertTrue(axis.handleKey(key(down = false, keyCode = KeyEvent.KEYCODE_DPAD_UP)))
        assertEquals(MappingCapturePhase.WAITING_FOR_AXIS, axis.state.value.phase)
        assertNull(axis.state.value.candidate)

        val analogPair = openedEditor()
        analogPair.beginAnalogPairCapture()
        assertTrue(analogPair.handleKey(key(down = false, keyCode = KeyEvent.KEYCODE_DPAD_UP)))
        assertEquals(MappingCapturePhase.WAITING_FOR_ANALOG_PAIR_X, analogPair.state.value.phase)
        assertNull(analogPair.state.value.candidate)
    }

    @Test fun pendingDpadReleaseStateClearsAtEveryCaptureLifecycleBoundary() = runTest {
        val up = key(down = false, keyCode = KeyEvent.KEYCODE_DPAD_UP)

        val newCapture = openedEditor()
        newCapture.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        newCapture.handleKey(key(keyCode = KeyEvent.KEYCODE_DPAD_UP))
        assertTrue(newCapture.shouldPreDispatchKey(up))
        newCapture.beginButtonCapture(N64SemanticTargetV1.DPAD_DOWN)
        assertTrue(newCapture.shouldPreDispatchKey(up)) // active capture owns eligible selected-device input
        newCapture.cancelCapture()
        assertFalse(newCapture.shouldPreDispatchKey(up))

        val cancelled = openedEditor()
        cancelled.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        cancelled.handleKey(key(keyCode = KeyEvent.KEYCODE_DPAD_UP))
        cancelled.cancelCapture()
        assertFalse(cancelled.shouldPreDispatchKey(up))

        val applied = openedEditor()
        applied.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        applied.handleKey(key(keyCode = KeyEvent.KEYCODE_DPAD_UP))
        applied.applyCandidate()
        assertFalse(applied.shouldPreDispatchKey(up))

        val closed = openedEditor()
        closed.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        closed.handleKey(key(keyCode = KeyEvent.KEYCODE_DPAD_UP))
        closed.requestClose()
        assertFalse(closed.shouldPreDispatchKey(up))

        val timedOut = openedEditor()
        timedOut.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        timedOut.timeoutCapture()
        assertFalse(timedOut.shouldPreDispatchKey(up))
    }

    @Test fun dpadUpFallbackUsesExistingConflictAndSuppressionSemantics() = runTest {
        val changes = mutableListOf<Boolean>()
        val editor = openedEditor(changes)
        editor.beginButtonCapture(N64SemanticTargetV1.B)
        assertTrue(editor.handleKey(key(down = false, keyCode = KeyEvent.KEYCODE_DPAD_UP)))
        assertEquals(MappingCapturePhase.CONFLICT, editor.state.value.phase)
        assertTrue(editor.state.value.conflicts.any { it.contains("N64 B") })
        assertTrue(editor.state.value.conflicts.any { it.contains("D-pad Up") })
        assertEquals(listOf(true, false), changes)
    }

    @Test fun dpadUpFallbackBindingPersistsThroughAuthoritativeSaveReload() = runTest {
        val repository = FakeEditorRepository(profile())
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", device())
        editor.beginButtonCapture(N64SemanticTargetV1.C_UP)
        editor.handleKey(key(down = false, keyCode = KeyEvent.KEYCODE_DPAD_UP))
        editor.applyCandidate()

        assertTrue(editor.save())
        assertEquals(1, repository.updateCount)
        assertEquals(
            KeyEvent.KEYCODE_DPAD_UP,
            editor.state.value.working!!.buttonBindings.single {
                it.target == N64SemanticTargetV1.C_UP
            }.keyCode,
        )
        assertEquals(editor.state.value.original, editor.state.value.working)
    }

    @Test fun capturePredispatchIsLimitedToSelectedEligibleDeviceAndActiveCapture() = runTest {
        val editor = openedEditor()
        val idle = editor.state.value
        assertFalse(shouldPreDispatchMappingCaptureKey(idle, 7, eligibleSource = true))

        editor.beginButtonCapture(N64SemanticTargetV1.DPAD_UP)
        val capturing = editor.state.value
        assertTrue(shouldPreDispatchMappingCaptureKey(capturing, 7, eligibleSource = true))
        assertFalse(shouldPreDispatchMappingCaptureKey(capturing, 8, eligibleSource = true))
        assertFalse(shouldPreDispatchMappingCaptureKey(capturing, 7, eligibleSource = false))

        editor.cancelCapture()
        assertFalse(shouldPreDispatchMappingCaptureKey(editor.state.value, 7, eligibleSource = true))
    }

    @Test fun existingTargetAndPhysicalReuseRequireExplicitReplace() = runTest {
        val editor = openedEditor()
        editor.beginButtonCapture(N64SemanticTargetV1.B)
        editor.handleKey(key(keyCode = KeyEvent.KEYCODE_BUTTON_A))
        assertEquals(MappingCapturePhase.CONFLICT, editor.state.value.phase)
        assertTrue(editor.state.value.conflicts.any { it.contains("N64 B") })
        assertTrue(editor.state.value.conflicts.any { it.contains("N64 A") })
        val before = editor.state.value.working
        assertEquals(before, editor.state.value.working)

        editor.applyCandidate()
        val updated = editor.state.value.working!!
        assertFalse(updated.buttonBindings.any { it.target == N64SemanticTargetV1.A })
        assertEquals(
            KeyEvent.KEYCODE_BUTTON_A,
            updated.buttonBindings.single { it.target == N64SemanticTargetV1.B }.keyCode,
        )
    }

    @Test fun cancelAndTimeoutLeaveWorkingCopyUnchangedAndBalanceSuppression() = runTest {
        val changes = mutableListOf<Boolean>()
        val editor = openedEditor(changes)
        val before = editor.state.value.working
        editor.beginButtonCapture(N64SemanticTargetV1.A)
        editor.cancelCapture()
        assertEquals(before, editor.state.value.working)
        editor.beginAxisCapture(N64SemanticTargetV1.C_UP)
        editor.timeoutCapture()
        assertEquals(before, editor.state.value.working)
        assertEquals(listOf(true, false, true, false), changes)
    }

    @Test fun clearingAndRestoringDefaultsStayInWorkingCopyAndPreserveMetadata() = runTest {
        val custom = profile().copy(profileName = "My Pro", identity = identity())
        val repository = FakeEditorRepository(custom)
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open(custom.profileId, device())
        editor.clearTarget(N64SemanticTargetV1.A)
        assertFalse(editor.state.value.working!!.buttonBindings.any { it.target == N64SemanticTargetV1.A })
        assertEquals(0, repository.updateCount)
        editor.requestRestoreDefaults()
        editor.confirmRestoreDefaults()
        val restored = editor.state.value.working!!
        assertEquals(custom.profileId, restored.profileId)
        assertEquals(custom.profileName, restored.profileName)
        assertEquals(custom.identity, restored.identity)
        assertEquals(StandardAndroidGamepadProfile.profile.buttonBindings, restored.buttonBindings)
        assertEquals(0, repository.updateCount)
    }

    @Test fun inversionDeadzoneScaleAndRenameAreValidatedWorkingCopyEdits() = runTest {
        val editor = openedEditor()
        val original = editor.state.value.working!!.axisBindings.single { it.target == N64SemanticTargetV1.ANALOG_Y }
        editor.toggleInversion(N64SemanticTargetV1.ANALOG_Y)
        editor.setDeadZone(N64SemanticTargetV1.ANALOG_Y, 0.2f)
        editor.setScale(N64SemanticTargetV1.ANALOG_Y, 1.5f)
        editor.setProfileName("Renamed")
        val updated = editor.state.value.working!!
        val analog = updated.axisBindings.single { it.target == N64SemanticTargetV1.ANALOG_Y }
        assertNotEquals(original.inverted, analog.inverted)
        assertEquals(0.2f, analog.deadZone)
        assertEquals(1.5f, analog.scale)
        assertEquals("Renamed", updated.profileName)
        assertTrue(editor.state.value.validationErrors.isEmpty())

        editor.setScale(N64SemanticTargetV1.ANALOG_Y, 5f)
        assertTrue(editor.state.value.validationErrors.any { it.contains("INVALID_SCALE") })
    }

    @Test fun invalidProfileCannotSaveAndValidProfileUpdatesExactlyOnceThenReloads() = runTest {
        val repository = FakeEditorRepository(profile())
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", device())
        editor.setProfileName(" ")
        assertFalse(editor.save())
        assertEquals(0, repository.updateCount)

        editor.setProfileName("Valid")
        editor.toggleInversion(N64SemanticTargetV1.ANALOG_Y)
        assertTrue(editor.save())
        assertEquals(1, repository.updateCount)
        assertFalse(editor.state.value.dirty)
        assertEquals(editor.state.value.original, editor.state.value.working)
        assertEquals("Profile saved and reloaded.", editor.state.value.statusMessage)
    }

    @Test fun repositoryFailureKeepsRecoverableWorkingCopy() = runTest {
        val repository = FakeEditorRepository(profile()).apply { failUpdate = true }
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", device())
        editor.setProfileName("Recoverable")
        assertFalse(editor.save())
        assertEquals("Recoverable", editor.state.value.working!!.profileName)
        assertTrue(editor.state.value.dirty)
        assertTrue(editor.state.value.errorMessage!!.contains("unavailable"))
    }

    @Test fun closeRequiresExplicitDiscardOnlyWhenDirty() = runTest {
        val editor = openedEditor()
        editor.setProfileName("Dirty")
        editor.requestClose()
        assertTrue(editor.state.value.active)
        assertTrue(editor.state.value.discardConfirmation)
        editor.continueEditing()
        assertTrue(editor.state.value.active)
        editor.requestClose()
        editor.discardAndClose()
        assertFalse(editor.state.value.active)
    }

    @Test fun disconnectCancelsCapturePreservesConfirmedEditsAndDoesNotRetarget() = runTest {
        val changes = mutableListOf<Boolean>()
        val editor = openedEditor(changes)
        editor.setProfileName("Kept")
        editor.beginButtonCapture(N64SemanticTargetV1.A)
        editor.updateDevices(BridgeControllerDiagnosticsPresentation())
        assertFalse(editor.state.value.captureActive)
        assertFalse(editor.state.value.deviceAvailable)
        assertEquals("Kept", editor.state.value.working!!.profileName)
        assertEquals(listOf(true, false), changes)
        assertFalse(editor.canChangeManagementDevice())
        editor.rejectManagementDeviceChange()
        assertTrue(editor.state.value.errorMessage!!.contains("Save or discard"))
    }

    @Test fun candidateCaptureSuppressionEndsBeforeApplyAndNeverWritesRepository() = runTest {
        val repository = FakeEditorRepository(profile())
        val changes = mutableListOf<Boolean>()
        val editor = ControllerMappingEditorCoordinator(repository, changes::add)
        editor.open("custom", device())
        editor.beginButtonCapture(N64SemanticTargetV1.A)
        editor.handleKey(key(keyCode = KeyEvent.KEYCODE_BUTTON_X))
        assertEquals(listOf(true, false), changes)
        assertEquals(0, repository.updateCount)
        editor.applyCandidate()
        assertEquals(0, repository.updateCount)
    }

    @Test fun reconnectWithNewRuntimeIdRequiresExplicitExactIdentityRecovery() = runTest {
        val editor = openedEditor()
        editor.setProfileName("Kept")
        editor.beginButtonCapture(N64SemanticTargetV1.A)
        editor.updateDevices(BridgeControllerDiagnosticsPresentation())
        assertFalse(editor.state.value.captureActive)
        assertFalse(editor.state.value.deviceAvailable)
        assertEquals("Kept", editor.state.value.working!!.profileName)

        val reconnected = device().copy(runtimeDeviceId = 101)
        editor.updateDevices(BridgeControllerDiagnosticsPresentation(devices = listOf(reconnected)))
        assertEquals(7, editor.state.value.managementRuntimeDeviceId)
        assertFalse(editor.state.value.deviceAvailable)
        assertEquals(101, editor.state.value.reconnectCandidate?.runtimeDeviceId)

        assertTrue(editor.recoverWithReconnectedController(101))
        assertEquals(101, editor.state.value.managementRuntimeDeviceId)
        assertTrue(editor.state.value.deviceAvailable)
        assertEquals("Kept", editor.state.value.working!!.profileName)
        assertEquals(identity(), editor.state.value.working!!.identity)

        assertTrue(editor.beginButtonCapture(N64SemanticTargetV1.B))
        assertTrue(editor.handleKey(key(deviceId = 101, keyCode = KeyEvent.KEYCODE_BUTTON_B)))
        assertEquals(KeyEvent.KEYCODE_BUTTON_B,
            (editor.state.value.candidate as MappingCaptureCandidate.Button).keyCode)
    }

    @Test fun unrelatedOrAmbiguousControllerCannotRecoverOpenEditor() = runTest {
        val editor = openedEditor()
        editor.updateDevices(BridgeControllerDiagnosticsPresentation())

        val unrelated = device().copy(runtimeDeviceId = 100, descriptor = "different-controller")
        editor.updateDevices(BridgeControllerDiagnosticsPresentation(devices = listOf(unrelated)))
        assertNull(editor.state.value.reconnectCandidate)
        assertFalse(editor.recoverWithReconnectedController(100))
        assertEquals(7, editor.state.value.managementRuntimeDeviceId)

        val first = device().copy(runtimeDeviceId = 101)
        val second = device().copy(runtimeDeviceId = 102)
        editor.updateDevices(BridgeControllerDiagnosticsPresentation(devices = listOf(first, second)))
        assertTrue(editor.state.value.reconnectAmbiguous)
        assertNull(editor.state.value.reconnectCandidate)
        assertFalse(editor.recoverWithReconnectedController(101))
        assertEquals(7, editor.state.value.managementRuntimeDeviceId)
    }

    @Test fun externallyDeletedProfileSaveFailsClosedAndRetainsWorkingCopy() = runTest {
        val repository = FakeEditorRepository(profile())
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", device())
        editor.setProfileName("Recoverable after deletion")
        repository.snapshot = repository.snapshot.copy(profiles = emptyList())

        assertFalse(editor.save())
        assertEquals(0, repository.updateCount)
        assertEquals("Recoverable after deletion", editor.state.value.working!!.profileName)
        assertTrue(editor.state.value.dirty)
        assertTrue(editor.state.value.errorMessage!!.contains("no longer exists"))
    }

    @Test fun saveInFlightRejectsDuplicateAndPerformsOneUpdateAndOneAuthoritativeReload() = runTest {
        val repository = DelayedEditorRepository(profile())
        val editor = ControllerMappingEditorCoordinator(repository)
        editor.open("custom", device())
        editor.setProfileName("Saved once")

        val first = async { editor.save() }
        repository.updateStarted.await()
        val duplicate = async { editor.save() }
        assertFalse(duplicate.await())
        repository.releaseUpdate.complete(Unit)

        assertTrue(first.await())
        assertEquals(1, repository.updateCount)
        assertEquals(1, repository.authoritativeReloadCount)
        assertFalse(editor.state.value.dirty)
        assertEquals("Profile saved and reloaded.", editor.state.value.statusMessage)
    }

    private suspend fun openedEditor(suppression: MutableList<Boolean> = mutableListOf()): ControllerMappingEditorCoordinator {
        val editor = ControllerMappingEditorCoordinator(FakeEditorRepository(profile()), suppression::add)
        editor.open("custom", device())
        return editor
    }

    private fun profile(): ControllerProfileV1 = StandardAndroidGamepadProfile.profile.copy(
        profileId = "custom",
        profileName = "Custom",
        identity = identity(),
    )

    private fun identity() = device().toStableIdentity()

    private fun device() = BridgeControllerDiagnosticDevice(
        runtimeDeviceId = 7,
        descriptor = "switch-pro-descriptor",
        name = "Nintendo Switch Pro Controller",
        vendorId = 0x057e,
        productId = 0x2009,
        sources = InputDevice.SOURCE_GAMEPAD or InputDevice.SOURCE_JOYSTICK,
        hasGamepadSource = true,
        hasJoystickSource = true,
        controllerNumber = 1,
        motionRanges = listOf(
            range(MotionEvent.AXIS_X), range(MotionEvent.AXIS_Y),
            range(MotionEvent.AXIS_Z), range(MotionEvent.AXIS_RZ),
            range(MotionEvent.AXIS_RX), range(MotionEvent.AXIS_RY),
            range(MotionEvent.AXIS_HAT_X), range(MotionEvent.AXIS_HAT_Y),
            BridgeControllerMotionRange(
                MotionEvent.AXIS_LTRIGGER, "AXIS_LTRIGGER", 0f, 1f, 0.05f,
                InputDevice.SOURCE_JOYSTICK,
            ),
        ),
    )

    private fun range(axis: Int) = BridgeControllerMotionRange(
        axis, "AXIS_$axis", -1f, 1f, 0.08f, InputDevice.SOURCE_JOYSTICK,
    )

    private fun key(
        deviceId: Int = 7,
        eligible: Boolean = true,
        down: Boolean = true,
        up: Boolean = !down,
        repeat: Int = 0,
        keyCode: Int = KeyEvent.KEYCODE_BUTTON_A,
    ) = MappingCaptureKeyInput(deviceId, eligible, down, up, repeat, keyCode)

    private fun axes(vararg values: Pair<Int, Float>) = MappingCaptureAxisInput(
        7,
        true,
        mapOf(*values),
    )

    private class FakeEditorRepository(profile: ControllerProfileV1) : ControllerProfileUiRepository {
        var snapshot = ControllerProfileUiRepositorySnapshot(listOf(profile), profile.profileId, profile.identity)
        var updateCount = 0
        var failUpdate = false
        override suspend fun load() = snapshot
        override suspend fun create(profile: ControllerProfileV1) = Unit
        override suspend fun update(profile: ControllerProfileV1) {
            if (failUpdate) throw ControllerProfileUiRepositoryException("Controller profile storage is unavailable.")
            if (snapshot.profiles.none { it.profileId == profile.profileId }) {
                throw ControllerProfileUiRepositoryException("The controller profile no longer exists.")
            }
            updateCount++
            snapshot = snapshot.copy(profiles = snapshot.profiles.map {
                if (it.profileId == profile.profileId) profile else it
            })
        }
        override suspend fun select(profileId: String?) = Unit
        override suspend fun delete(profileId: String) = Unit
        override suspend fun selectController(identity: com.melfegor.nice64.core.input.ControllerIdentityV1?) = Unit
    }

    private class DelayedEditorRepository(profile: ControllerProfileV1) : ControllerProfileUiRepository {
        var snapshot = ControllerProfileUiRepositorySnapshot(listOf(profile), profile.profileId, profile.identity)
        var updateCount = 0
        var authoritativeReloadCount = 0
        private var opened = false
        val updateStarted = CompletableDeferred<Unit>()
        val releaseUpdate = CompletableDeferred<Unit>()

        override suspend fun load(): ControllerProfileUiRepositorySnapshot {
            if (opened) authoritativeReloadCount++ else opened = true
            return snapshot
        }

        override suspend fun create(profile: ControllerProfileV1) = Unit
        override suspend fun update(profile: ControllerProfileV1) {
            updateCount++
            updateStarted.complete(Unit)
            releaseUpdate.await()
            snapshot = snapshot.copy(profiles = listOf(profile))
        }
        override suspend fun select(profileId: String?) = Unit
        override suspend fun delete(profileId: String) = Unit
        override suspend fun selectController(identity: com.melfegor.nice64.core.input.ControllerIdentityV1?) = Unit
    }
}
