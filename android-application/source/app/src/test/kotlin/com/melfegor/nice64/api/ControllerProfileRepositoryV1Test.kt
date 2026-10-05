package com.melfegor.nice64.api

import com.melfegor.nice64.core.input.AxisBindingV1
import com.melfegor.nice64.core.input.AxisDirectionV1
import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.ControllerSourceClassV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import kotlinx.coroutines.runBlocking
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue
import org.junit.Test

class ControllerProfileRepositoryV1Test {
    @Test fun emptyStoreLoadsEmptyDocumentAndCodeOnlyFallback(): Unit = runBlocking {
        val store = MemoryStore()
        val repository = ControllerProfileRepositoryV1(store)
        val loaded = assertIs<ControllerProfilesLoadResultV1.Success>(repository.loadDocument())
        assertTrue(loaded.document.profiles.isEmpty())
        assertIs<ControllerProfileResolutionV1.StandardAndroidGamepadFallback>(
            success(repository.resolve(identity("device"))),
        )
        assertNull(store.raw)
    }

    @Test fun saveMultipleListAndCanonicalOrder(): Unit = runBlocking {
        val store = MemoryStore()
        val repository = ControllerProfileRepositoryV1(store)
        assertIs<ControllerProfileRepositoryResultV1.Success<*>>(repository.saveProfile(profile("z")))
        assertIs<ControllerProfileRepositoryResultV1.Success<*>>(repository.saveProfile(profile("a")))
        assertEquals(listOf("a", "z"), success(repository.listProfiles()).map { it.profileId })
        assertTrue(store.raw!!.indexOf("\"profileId\":\"a\"") < store.raw!!.indexOf("\"profileId\":\"z\""))
    }

    @Test fun updateRequiresExplicitModeAndKeepsId(): Unit = runBlocking {
        val repository = ControllerProfileRepositoryV1(MemoryStore())
        repository.saveProfile(profile("one"))
        assertFailure(repository.saveProfile(profile("one", "Changed")),
            ControllerProfileRepositoryErrorV1.DUPLICATE_PROFILE_ID)
        assertIs<ControllerProfileRepositoryResultV1.Success<*>>(
            repository.saveProfile(profile("one", "Changed"), ControllerProfileSaveModeV1.UPDATE),
        )
        assertEquals("Changed", success(repository.listProfiles()).single().profileName)
        assertFailure(repository.saveProfile(profile("missing"), ControllerProfileSaveModeV1.UPDATE),
            ControllerProfileRepositoryErrorV1.PROFILE_NOT_FOUND)
    }

    @Test fun deleteAndSelectedProfilePersistSafely(): Unit = runBlocking {
        val store = MemoryStore()
        val repository = ControllerProfileRepositoryV1(store)
        repository.saveProfile(profile("one"))
        repository.setSelectedProfile("one")
        assertEquals("one", success(repository.getSelectedProfile())!!.profileId)
        assertTrue(success(repository.deleteProfile("one")))
        assertNull(success(repository.getSelectedProfile()))
        assertNull(assertIs<ControllerProfilesLoadResultV1.Success>(repository.loadDocument()).document.selectedProfileId)
    }

    @Test fun selectedProfileAndStableControllerIdentityRoundTrip(): Unit = runBlocking {
        val store = MemoryStore()
        val first = ControllerProfileRepositoryV1(store)
        first.saveProfile(profile("one"))
        first.setSelectedProfile("one")
        first.setSelectedControllerIdentity(identity("descriptor"))
        val reopened = ControllerProfileRepositoryV1(store)
        val document = assertIs<ControllerProfilesLoadResultV1.Success>(reopened.loadDocument()).document
        assertEquals("one", document.selectedProfileId)
        assertEquals("descriptor", document.selectedControllerIdentity?.descriptor)
        assertFalse(store.raw!!.contains("deviceId", ignoreCase = true))
        assertFalse(store.raw!!.contains("controllerNumber", ignoreCase = true))
    }

    @Test fun canonicalCodecRoundTripIsByteStable() {
        val document = ControllerProfilesDocumentV1(
            profiles = listOf(profile("b"), profile("a")),
            selectedProfileId = "a",
            selectedControllerIdentity = identity("selected"),
        )
        val first = ControllerProfilesCodecV1.encode(document)
        val decoded = assertIs<ControllerProfilesLoadResultV1.Success>(ControllerProfilesCodecV1.decode(first))
        val second = ControllerProfilesCodecV1.encode(decoded.document)
        assertEquals(first, second)
    }

    @Test fun invalidProfileRejectedBeforeAtomicSave(): Unit = runBlocking {
        val store = MemoryStore()
        val repository = ControllerProfileRepositoryV1(store)
        repository.saveProfile(profile("good"))
        val before = store.raw
        val invalid = profile("bad").copy(axisBindings = listOf(
            AxisBindingV1(1, AxisDirectionV1.BOTH, N64SemanticTargetV1.Z),
        ))
        assertFailure(repository.saveProfile(invalid), ControllerProfileRepositoryErrorV1.INVALID_PROFILE)
        assertEquals(before, store.raw)
    }

    @Test fun malformedStoredProfileIsSkippedWhileValidSurvives() {
        val validJson = ControllerProfilesCodecV1.encode(ControllerProfilesDocumentV1(
            profiles = listOf(profile("good"), profile("bad")),
        ))
        val malformed = validJson.replaceFirst(
            "\"profileId\":\"bad\"",
            "\"profileId\":\"bad\",\"unexpected\":true",
        )
        val loaded = assertIs<ControllerProfilesLoadResultV1.Success>(ControllerProfilesCodecV1.decode(malformed))
        assertEquals(listOf("good"), loaded.document.profiles.map { it.profileId })
        assertEquals(listOf("bad"), loaded.skippedInvalidProfileIds)
    }

    @Test fun duplicateStoredIdsRejected() {
        val raw = ControllerProfilesCodecV1.encode(ControllerProfilesDocumentV1(
            profiles = listOf(profile("same", "One"), profile("same", "Two")),
        ))
        assertIs<ControllerProfilesLoadResultV1.DuplicateProfileIds>(ControllerProfilesCodecV1.decode(raw))
    }

    @Test fun futureSchemaAndCorruptJsonFailClosedWithoutOverwrite(): Unit = runBlocking {
        val future = "{\"schemaVersion\":2,\"profiles\":[],\"selectedProfileId\":null," +
            "\"selectedControllerIdentity\":null}"
        val futureStore = MemoryStore(future)
        val futureRepository = ControllerProfileRepositoryV1(futureStore)
        assertIs<ControllerProfilesLoadResultV1.UnsupportedVersion>(futureRepository.loadDocument())
        assertFailure(futureRepository.saveProfile(profile("x")), ControllerProfileRepositoryErrorV1.UNSUPPORTED_SCHEMA)
        assertEquals(future, futureStore.raw)

        val corruptStore = MemoryStore("{broken")
        val corruptRepository = ControllerProfileRepositoryV1(corruptStore)
        assertIs<ControllerProfilesLoadResultV1.Corrupt>(corruptRepository.loadDocument())
        assertFailure(corruptRepository.saveProfile(profile("x")), ControllerProfileRepositoryErrorV1.CORRUPT_STORAGE)
        assertEquals("{broken", corruptStore.raw)
    }

    @Test fun exactDescriptorVidPidWins() {
        val candidate = identity("same", name = "different", motion = "9")
        val match = ControllerProfileMatcherV1.match(candidate, listOf(profile("exact", identity = identity("same"))))
        assertEquals(ControllerProfileMatchResultV1.ExactMatch("exact"), match)
    }

    @Test fun fallbackRequiresVidPidNameAndMotionSignature() {
        val stored = identity("old", name = "pro controller", motion = "0,1,11,14")
        val candidate = identity("new", name = "pro controller", motion = "0,1,11,14")
        assertEquals(ControllerProfileMatchResultV1.FallbackMatch("fallback"),
            ControllerProfileMatcherV1.match(candidate, listOf(profile("fallback", identity = stored))))
    }

    @Test fun descriptorCollisionAndFallbackCollisionAreExplicitlyAmbiguous() {
        val exactProfiles = listOf(profile("b", identity = identity("same")), profile("a", identity = identity("same")))
        assertEquals(ControllerProfileMatchResultV1.Ambiguous(listOf("a", "b")),
            ControllerProfileMatcherV1.match(identity("same"), exactProfiles))
        val fallbackProfiles = listOf(
            profile("d", identity = identity("one")), profile("c", identity = identity("two")),
        )
        assertEquals(ControllerProfileMatchResultV1.Ambiguous(listOf("c", "d")),
            ControllerProfileMatcherV1.match(identity("three"), fallbackProfiles))
    }

    @Test fun blankDescriptorNoFuzzyNameOrMotionMatching() {
        val stored = identity("", name = "Nintendo Controller", motion = "0,1")
        assertIs<ControllerProfileMatchResultV1.NoMatch>(ControllerProfileMatcherV1.match(
            identity("", name = "nintendo control", motion = "0,1"),
            listOf(profile("one", identity = stored)),
        ))
        assertIs<ControllerProfileMatchResultV1.NoMatch>(ControllerProfileMatcherV1.match(
            identity("", name = "nintendo controller", motion = "0,1,11"),
            listOf(profile("one", identity = stored)),
        ))
    }

    @Test fun motionSignatureAndNameNormalizationAreDeterministic() {
        assertEquals("0,1,11,14", ControllerMotionSignatureV1.fromAxisCodes(listOf(14, 1, 0, 11, 1)))
        assertEquals("nintendo switch pro controller",
            ControllerIdentityNormalizerV1.normalizeName("  Nintendo   Switch Pro Controller "))
    }

    @Test fun keyboardMouseOnlyIsIneligibleAndDefaultIsNeverStored(): Unit = runBlocking {
        val repository = ControllerProfileRepositoryV1(MemoryStore())
        assertNull(ControllerSourceClassV1.fromSources(false, false))
        assertIs<ControllerProfileResolutionV1.Ineligible>(success(repository.resolve(null)))
        assertTrue(success(repository.listProfiles()).isEmpty())
    }

    @Test fun staleSelectedProfileFallsBackToNullWithoutRewriting() {
        val raw = ControllerProfilesCodecV1.encode(ControllerProfilesDocumentV1(selectedProfileId = "missing"))
        val loaded = assertIs<ControllerProfilesLoadResultV1.Success>(ControllerProfilesCodecV1.decode(raw))
        assertNull(loaded.document.selectedProfileId)
    }

    private class MemoryStore(initial: String? = null) : ControllerProfileDocumentStoreV1 {
        var raw: String? = initial
        override suspend fun read(): String? = raw
        override suspend fun update(transform: (String?) -> String?): String? {
            raw = transform(raw)
            return raw
        }
    }

    private fun profile(
        id: String,
        name: String = "Profile $id",
        identity: ControllerIdentityV1 = identity("descriptor-$id"),
    ): ControllerProfileV1 = StandardAndroidGamepadProfile.profile.copy(
        profileId = id,
        profileName = name,
        identity = identity,
    )

    private fun identity(
        descriptor: String,
        name: String = "controller",
        motion: String = "0,1,11,14",
    ) = ControllerIdentityV1(
        descriptor = descriptor,
        vendorId = 0x057e,
        productId = 0x2009,
        normalizedName = ControllerIdentityNormalizerV1.normalizeName(name),
        sourceClass = ControllerSourceClassV1.GAMEPAD_AND_JOYSTICK,
        motionSignature = motion,
    )

    private fun <T> success(result: ControllerProfileRepositoryResultV1<T>): T =
        assertIs<ControllerProfileRepositoryResultV1.Success<T>>(result).value

    private fun assertFailure(
        result: ControllerProfileRepositoryResultV1<*>,
        expected: ControllerProfileRepositoryErrorV1,
    ) = assertEquals(expected, assertIs<ControllerProfileRepositoryResultV1.Failure>(result).error)
}
