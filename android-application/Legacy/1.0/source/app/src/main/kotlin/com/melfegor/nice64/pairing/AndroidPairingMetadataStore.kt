package com.melfegor.nice64.pairing

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.melfegor.nice64.core.model.BridgePairingMetadata
import kotlinx.coroutines.flow.first

private val Context.nice64PairingDataStore by preferencesDataStore(
    name = "nice64_pairing_profiles",
)

internal class AndroidPairingMetadataStore(
    private val dataStore: DataStore<Preferences>,
) : PairingMetadataStore {
    override suspend fun list(): List<BridgePairingMetadata> {
        val preferences = dataStore.data.first()
        return preferences[PROFILE_IDS].orEmpty()
            .map { profileId -> readMetadata(preferences, profileId) }
            .sortedByDescending(BridgePairingMetadata::pairedAtEpochMillis)
    }

    override suspend fun find(profileId: String): BridgePairingMetadata? {
        val preferences = dataStore.data.first()
        if (profileId !in preferences[PROFILE_IDS].orEmpty()) return null
        return readMetadata(preferences, profileId)
    }

    override suspend fun activeProfileId(): String? {
        val preferences = dataStore.data.first()
        val active = preferences[ACTIVE_PROFILE_ID] ?: return null
        return active.takeIf { it in preferences[PROFILE_IDS].orEmpty() }
    }

    override suspend fun upsert(metadata: BridgePairingMetadata, makeActive: Boolean) {
        requireProfileId(metadata.profileId)
        dataStore.edit { preferences ->
            preferences[PROFILE_IDS] =
                preferences[PROFILE_IDS].orEmpty() + metadata.profileId
            preferences[key(metadata.profileId, "label")] = metadata.displayLabel
            preferences[key(metadata.profileId, "url")] = metadata.apiBaseUrl
            preferences[key(metadata.profileId, "apiVersion")] = metadata.apiVersion
            preferences[key(metadata.profileId, "spki")] = metadata.spkiSha256
            preferences[key(metadata.profileId, "certificate")] =
                metadata.certificateSha256
            preferences[longKey(metadata.profileId, "pairedAt")] =
                metadata.pairedAtEpochMillis
            if (makeActive) preferences[ACTIVE_PROFILE_ID] = metadata.profileId
        }
    }

    override suspend fun selectActive(profileId: String): Boolean {
        requireProfileId(profileId)
        var selected = false
        dataStore.edit { preferences ->
            if (profileId in preferences[PROFILE_IDS].orEmpty()) {
                preferences[ACTIVE_PROFILE_ID] = profileId
                selected = true
            }
        }
        return selected
    }

    override suspend fun delete(profileId: String): Boolean {
        requireProfileId(profileId)
        var deleted = false
        dataStore.edit { preferences ->
            val ids = preferences[PROFILE_IDS].orEmpty()
            if (profileId !in ids) return@edit
            deleted = true
            preferences[PROFILE_IDS] = ids - profileId
            preferences.remove(key(profileId, "label"))
            preferences.remove(key(profileId, "url"))
            preferences.remove(key(profileId, "apiVersion"))
            preferences.remove(key(profileId, "spki"))
            preferences.remove(key(profileId, "certificate"))
            preferences.remove(longKey(profileId, "pairedAt"))
            if (preferences[ACTIVE_PROFILE_ID] == profileId) {
                preferences.remove(ACTIVE_PROFILE_ID)
            }
        }
        return deleted
    }

    private fun readMetadata(
        preferences: Preferences,
        profileId: String,
    ): BridgePairingMetadata = BridgePairingMetadata(
        profileId = profileId,
        displayLabel = preferences[key(profileId, "label")] ?: corrupt(),
        apiBaseUrl = preferences[key(profileId, "url")] ?: corrupt(),
        apiVersion = preferences[key(profileId, "apiVersion")] ?: corrupt(),
        spkiSha256 = preferences[key(profileId, "spki")] ?: corrupt(),
        certificateSha256 = preferences[key(profileId, "certificate")] ?: corrupt(),
        pairedAtEpochMillis = preferences[longKey(profileId, "pairedAt")] ?: corrupt(),
    )

    private fun corrupt(): Nothing = throw IllegalStateException(
        "Pairing metadata is incomplete.",
    )

    private fun requireProfileId(profileId: String) {
        require(PROFILE_ID_PATTERN.matches(profileId)) { "Invalid local profile ID." }
    }

    private fun key(profileId: String, field: String) =
        stringPreferencesKey("profile.$profileId.$field")

    private fun longKey(profileId: String, field: String) =
        longPreferencesKey("profile.$profileId.$field")

    companion object {
        private val PROFILE_IDS = stringSetPreferencesKey("profile_ids")
        private val ACTIVE_PROFILE_ID = stringPreferencesKey("active_profile_id")
        private val PROFILE_ID_PATTERN = Regex("[A-Za-z0-9-]{1,64}")

        fun create(context: Context): AndroidPairingMetadataStore =
            AndroidPairingMetadataStore(context.nice64PairingDataStore)
    }
}
