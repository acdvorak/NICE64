package com.melfegor.nice64.api

import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.preferences.preferencesDataStoreFile
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerSourceClassV1
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class ControllerProfilePersistenceInstrumentedTest {
    @Test
    fun dedicatedDataStorePersistsCollectionSelectionDeleteAndRepositoryRecreation(): Unit = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val name = "p3-controller-${UUID.randomUUID()}"
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val dataStore = PreferenceDataStoreFactory.create(
            scope = scope,
            produceFile = { context.preferencesDataStoreFile(name) },
        )
        val store = AndroidControllerProfileDocumentStoreV1(dataStore)
        try {
            val first = ControllerProfileRepositoryV1(store)
            assertTrue(first.saveProfile(profile("one")) is ControllerProfileRepositoryResultV1.Success)
            assertTrue(first.saveProfile(profile("two")) is ControllerProfileRepositoryResultV1.Success)
            assertTrue(first.setSelectedProfile("two") is ControllerProfileRepositoryResultV1.Success)

            val recreated = ControllerProfileRepositoryV1(store)
            assertEquals(listOf("one", "two"), success(recreated.listProfiles()).map { it.profileId })
            assertEquals("two", success(recreated.getSelectedProfile())?.profileId)
            assertTrue(recreated.deleteProfile("two") is ControllerProfileRepositoryResultV1.Success)

            val afterDelete = ControllerProfileRepositoryV1(store)
            assertEquals(listOf("one"), success(afterDelete.listProfiles()).map { it.profileId })
            assertNull(success(afterDelete.getSelectedProfile()))
            assertFalse(context.preferencesDataStoreFile(name).name.contains("pairing", ignoreCase = true))
            assertEquals("nice64_controller_profiles_v1", CONTROLLER_PROFILE_DATASTORE_NAME)
            assertFalse(CONTROLLER_PROFILE_DATASTORE_NAME == "nice64_pairing_profiles")
        } finally {
            scope.cancel()
        }
    }

    @Test
    fun malformedAndFutureDocumentsRemainFailClosedAcrossRepositoryRecreation(): Unit = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val dataStore = PreferenceDataStoreFactory.create(
            scope = scope,
            produceFile = { context.preferencesDataStoreFile("p3-failure-${UUID.randomUUID()}") },
        )
        val store = AndroidControllerProfileDocumentStoreV1(dataStore)
        try {
            store.update { "{broken" }
            assertTrue(ControllerProfileRepositoryV1(store).loadDocument() is ControllerProfilesLoadResultV1.Corrupt)
            store.update {
                "{\"schemaVersion\":99,\"profiles\":[],\"selectedProfileId\":null," +
                    "\"selectedControllerIdentity\":null}"
            }
            assertTrue(
                ControllerProfileRepositoryV1(store).loadDocument() is
                    ControllerProfilesLoadResultV1.UnsupportedVersion,
            )
        } finally {
            scope.cancel()
        }
    }

    private fun profile(id: String) = StandardAndroidGamepadProfile.profile.copy(
        profileId = id,
        profileName = "Profile $id",
        identity = ControllerIdentityV1(
            descriptor = "descriptor-$id",
            vendorId = 0x057e,
            productId = 0x2009,
            normalizedName = "nintendo switch pro controller",
            sourceClass = ControllerSourceClassV1.GAMEPAD_AND_JOYSTICK,
            motionSignature = "0,1,11,14",
        ),
    )

    private fun <T> success(result: ControllerProfileRepositoryResultV1<T>): T =
        (result as ControllerProfileRepositoryResultV1.Success<T>).value
}
