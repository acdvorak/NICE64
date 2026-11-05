package com.melfegor.nice64.api

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first

internal data class MouseInputPreferences(
    val invertX: Boolean = false,
    val invertY: Boolean = false,
)

internal interface MouseInputPreferencesStore {
    suspend fun load(): MouseInputPreferences
    suspend fun save(preferences: MouseInputPreferences)
}

private val Context.nice64MouseInputDataStore by preferencesDataStore(
    name = "nice64_mouse_input_v1",
)

internal class AndroidMouseInputPreferencesStore(
    private val dataStore: DataStore<Preferences>,
) : MouseInputPreferencesStore {
    override suspend fun load(): MouseInputPreferences {
        val values = dataStore.data.first()
        return MouseInputPreferences(
            invertX = values[INVERT_X] ?: false,
            invertY = values[INVERT_Y] ?: false,
        )
    }

    override suspend fun save(preferences: MouseInputPreferences) {
        dataStore.edit { values ->
            values[INVERT_X] = preferences.invertX
            values[INVERT_Y] = preferences.invertY
        }
    }

    companion object {
        private val INVERT_X = booleanPreferencesKey("invert_x")
        private val INVERT_Y = booleanPreferencesKey("invert_y")

        fun create(context: Context) =
            AndroidMouseInputPreferencesStore(context.nice64MouseInputDataStore)
    }
}
