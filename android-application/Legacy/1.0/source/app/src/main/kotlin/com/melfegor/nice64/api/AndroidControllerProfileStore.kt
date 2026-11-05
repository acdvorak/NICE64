package com.melfegor.nice64.api

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first

internal const val CONTROLLER_PROFILE_DATASTORE_NAME = "nice64_controller_profiles_v1"

private val Context.nice64ControllerProfileDataStore by preferencesDataStore(
    name = CONTROLLER_PROFILE_DATASTORE_NAME,
)

internal class AndroidControllerProfileDocumentStoreV1(
    private val dataStore: DataStore<Preferences>,
) : ControllerProfileDocumentStoreV1 {
    override suspend fun read(): String? = dataStore.data.first()[DOCUMENT]

    override suspend fun update(transform: (String?) -> String?): String? {
        var updated: String? = null
        dataStore.edit { preferences ->
            updated = transform(preferences[DOCUMENT])
            if (updated == null) preferences.remove(DOCUMENT) else preferences[DOCUMENT] = updated!!
        }
        return updated
    }

    companion object {
        private val DOCUMENT = stringPreferencesKey("controller_profiles_document_v1")

        fun create(context: Context) =
            AndroidControllerProfileDocumentStoreV1(context.nice64ControllerProfileDataStore)
    }
}

internal object ControllerProfileRepositoryFactoryV1 {
    fun create(context: Context) = ControllerProfileRepositoryV1(
        AndroidControllerProfileDocumentStoreV1.create(context),
    )
}
