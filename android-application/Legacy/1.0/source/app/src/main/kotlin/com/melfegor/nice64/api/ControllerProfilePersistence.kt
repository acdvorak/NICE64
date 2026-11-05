package com.melfegor.nice64.api

import com.melfegor.nice64.core.input.AxisBindingAlternativeV1
import com.melfegor.nice64.core.input.AxisBindingV1
import com.melfegor.nice64.core.input.AxisDirectionV1
import com.melfegor.nice64.core.input.ButtonBindingV1
import com.melfegor.nice64.core.input.CONTROLLER_PROFILE_SCHEMA_V1
import com.melfegor.nice64.core.input.ControllerIdentityV1
import com.melfegor.nice64.core.input.ControllerProfileV1
import com.melfegor.nice64.core.input.ControllerProfileValidatorV1
import com.melfegor.nice64.core.input.ControllerSourceClassV1
import com.melfegor.nice64.core.input.N64SemanticTargetV1
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

internal const val CONTROLLER_PROFILES_DOCUMENT_SCHEMA_V1 = 1

data class ControllerProfilesDocumentV1(
    val schemaVersion: Int = CONTROLLER_PROFILES_DOCUMENT_SCHEMA_V1,
    val profiles: List<ControllerProfileV1> = emptyList(),
    val selectedProfileId: String? = null,
    val selectedControllerIdentity: ControllerIdentityV1? = null,
)

sealed interface ControllerProfilesLoadResultV1 {
    data class Success(
        val document: ControllerProfilesDocumentV1,
        val skippedInvalidProfileIds: List<String> = emptyList(),
    ) : ControllerProfilesLoadResultV1
    data class UnsupportedVersion(val storedVersion: Int) : ControllerProfilesLoadResultV1
    data object Corrupt : ControllerProfilesLoadResultV1
    data object DuplicateProfileIds : ControllerProfilesLoadResultV1
}

enum class ControllerProfileRepositoryErrorV1 {
    INVALID_PROFILE,
    DUPLICATE_PROFILE_ID,
    PROFILE_NOT_FOUND,
    CORRUPT_STORAGE,
    UNSUPPORTED_SCHEMA,
    DUPLICATE_STORED_IDS,
    STORAGE_FAILURE,
}

sealed interface ControllerProfileRepositoryResultV1<out T> {
    data class Success<T>(val value: T) : ControllerProfileRepositoryResultV1<T>
    data class Failure(val error: ControllerProfileRepositoryErrorV1) : ControllerProfileRepositoryResultV1<Nothing>
}

enum class ControllerProfileSaveModeV1 { CREATE, UPDATE }

sealed interface ControllerProfileMatchResultV1 {
    data class ExactMatch(val profileId: String) : ControllerProfileMatchResultV1
    data class FallbackMatch(val profileId: String) : ControllerProfileMatchResultV1
    data class Ambiguous(val candidateIds: List<String>) : ControllerProfileMatchResultV1
    data object NoMatch : ControllerProfileMatchResultV1
}

sealed interface ControllerProfileResolutionV1 {
    data class Custom(val match: ControllerProfileMatchResultV1) : ControllerProfileResolutionV1
    data object StandardAndroidGamepadFallback : ControllerProfileResolutionV1
    data object Ineligible : ControllerProfileResolutionV1
}

object ControllerMotionSignatureV1 {
    fun fromAxisCodes(axisCodes: Iterable<Int>): String =
        axisCodes.distinct().sorted().joinToString(",")
}

object ControllerIdentityNormalizerV1 {
    fun normalizeName(name: String): String = name.trim().lowercase()
        .replace(Regex("\\s+"), " ")
}

object ControllerProfileMatcherV1 {
    fun match(
        identity: ControllerIdentityV1,
        profiles: List<ControllerProfileV1>,
    ): ControllerProfileMatchResultV1 {
        val identified = profiles.mapNotNull { profile -> profile.identity?.let { profile to it } }
        val descriptorMatches = if (identity.descriptor.isBlank()) emptyList() else identified.filter { (_, stored) ->
            stored.descriptor.isNotBlank() && stored.descriptor == identity.descriptor &&
                stored.vendorId == identity.vendorId && stored.productId == identity.productId
        }.map { it.first.profileId }.sorted()
        if (descriptorMatches.size == 1) return ControllerProfileMatchResultV1.ExactMatch(descriptorMatches.single())
        if (descriptorMatches.size > 1) return ControllerProfileMatchResultV1.Ambiguous(descriptorMatches)

        val fallbackMatches = identified.filter { (_, stored) ->
            stored.vendorId == identity.vendorId && stored.productId == identity.productId &&
                stored.normalizedName == identity.normalizedName &&
                stored.motionSignature == identity.motionSignature
        }.map { it.first.profileId }.sorted()
        return when (fallbackMatches.size) {
            0 -> ControllerProfileMatchResultV1.NoMatch
            1 -> ControllerProfileMatchResultV1.FallbackMatch(fallbackMatches.single())
            else -> ControllerProfileMatchResultV1.Ambiguous(fallbackMatches)
        }
    }
}

internal interface ControllerProfileDocumentStoreV1 {
    suspend fun read(): String?
    suspend fun update(transform: (String?) -> String?): String?
}

internal object ControllerProfilesCodecV1 {
    private val json = Json {
        encodeDefaults = true
        explicitNulls = true
        ignoreUnknownKeys = false
    }

    fun encode(document: ControllerProfilesDocumentV1): String {
        val canonical = DocumentDto(
            schemaVersion = CONTROLLER_PROFILES_DOCUMENT_SCHEMA_V1,
            profiles = document.profiles.sortedBy { it.profileId }.map(ProfileDto::fromDomain),
            selectedProfileId = document.selectedProfileId,
            selectedControllerIdentity = document.selectedControllerIdentity?.let(IdentityDto::fromDomain),
        )
        return json.encodeToString(DocumentDto.serializer(), canonical)
    }

    fun decode(source: String?): ControllerProfilesLoadResultV1 {
        if (source == null) return ControllerProfilesLoadResultV1.Success(ControllerProfilesDocumentV1())
        val root = runCatching { json.parseToJsonElement(source).jsonObject }.getOrNull()
            ?: return ControllerProfilesLoadResultV1.Corrupt
        val version = root["schemaVersion"]?.jsonPrimitive?.intOrNull
            ?: return ControllerProfilesLoadResultV1.Corrupt
        if (version > CONTROLLER_PROFILES_DOCUMENT_SCHEMA_V1) {
            return ControllerProfilesLoadResultV1.UnsupportedVersion(version)
        }
        if (version != CONTROLLER_PROFILES_DOCUMENT_SCHEMA_V1) return ControllerProfilesLoadResultV1.Corrupt
        val profileElements = runCatching { root.getValue("profiles").jsonArray }.getOrNull()
            ?: return ControllerProfilesLoadResultV1.Corrupt
        val valid = mutableListOf<ControllerProfileV1>()
        val skipped = mutableListOf<String>()
        profileElements.forEachIndexed { index, element ->
            val dto = runCatching { json.decodeFromJsonElement<ProfileDto>(element) }.getOrNull()
            val profile = runCatching { dto?.toDomain() }.getOrNull()
            if (profile == null || !ControllerProfileValidatorV1.validate(profile).isValid) {
                skipped += dto?.profileId
                    ?: runCatching { element.jsonObject["profileId"]?.jsonPrimitive?.content }.getOrNull()
                    ?: "entry-$index"
            } else valid += profile
        }
        if (valid.groupingBy { it.profileId }.eachCount().any { it.value > 1 }) {
            return ControllerProfilesLoadResultV1.DuplicateProfileIds
        }
        val selectedProfileId = primitiveStringOrNull(root["selectedProfileId"])
            ?.takeIf { id -> valid.any { it.profileId == id } }
        val selectedIdentity = when (val element = root["selectedControllerIdentity"]) {
            null, JsonNull -> null
            else -> runCatching { json.decodeFromJsonElement<IdentityDto>(element).toDomain() }.getOrNull()
                ?: return ControllerProfilesLoadResultV1.Corrupt
        }
        return ControllerProfilesLoadResultV1.Success(
            ControllerProfilesDocumentV1(
                profiles = valid.sortedBy { it.profileId },
                selectedProfileId = selectedProfileId,
                selectedControllerIdentity = selectedIdentity,
            ),
            skipped.sorted(),
        )
    }

    private fun primitiveStringOrNull(element: JsonElement?): String? = when (element) {
        null, JsonNull -> null
        else -> runCatching { element.jsonPrimitive.content }.getOrNull()
    }
}

internal class ControllerProfileRepositoryV1(
    private val store: ControllerProfileDocumentStoreV1,
) {
    suspend fun loadDocument(): ControllerProfilesLoadResultV1 = runCatching {
        ControllerProfilesCodecV1.decode(store.read())
    }.getOrElse { ControllerProfilesLoadResultV1.Corrupt }

    suspend fun listProfiles(): ControllerProfileRepositoryResultV1<List<ControllerProfileV1>> =
        loadSuccess { it.profiles }

    suspend fun saveProfile(
        profile: ControllerProfileV1,
        mode: ControllerProfileSaveModeV1 = ControllerProfileSaveModeV1.CREATE,
    ): ControllerProfileRepositoryResultV1<ControllerProfileV1> {
        if (!ControllerProfileValidatorV1.validate(profile).isValid) {
            return ControllerProfileRepositoryResultV1.Failure(ControllerProfileRepositoryErrorV1.INVALID_PROFILE)
        }
        return mutate { document ->
            val existing = document.profiles.indexOfFirst { it.profileId == profile.profileId }
            when {
                mode == ControllerProfileSaveModeV1.CREATE && existing >= 0 ->
                    Mutation.Failure(ControllerProfileRepositoryErrorV1.DUPLICATE_PROFILE_ID)
                mode == ControllerProfileSaveModeV1.UPDATE && existing < 0 ->
                    Mutation.Failure(ControllerProfileRepositoryErrorV1.PROFILE_NOT_FOUND)
                else -> {
                    val profiles = document.profiles.toMutableList()
                    if (existing >= 0) profiles[existing] = profile else profiles += profile
                    Mutation.Success(document.copy(profiles = profiles.sortedBy { it.profileId }), profile)
                }
            }
        }
    }

    suspend fun deleteProfile(profileId: String): ControllerProfileRepositoryResultV1<Boolean> = mutate { document ->
        if (document.profiles.none { it.profileId == profileId }) {
            Mutation.Failure(ControllerProfileRepositoryErrorV1.PROFILE_NOT_FOUND)
        } else Mutation.Success(
            document.copy(
                profiles = document.profiles.filterNot { it.profileId == profileId },
                selectedProfileId = document.selectedProfileId?.takeUnless { it == profileId },
            ),
            true,
        )
    }

    suspend fun setSelectedProfile(profileId: String?): ControllerProfileRepositoryResultV1<String?> = mutate { document ->
        if (profileId != null && document.profiles.none { it.profileId == profileId }) {
            Mutation.Failure(ControllerProfileRepositoryErrorV1.PROFILE_NOT_FOUND)
        } else Mutation.Success(document.copy(selectedProfileId = profileId), profileId)
    }

    suspend fun getSelectedProfile(): ControllerProfileRepositoryResultV1<ControllerProfileV1?> =
        loadSuccess { document -> document.profiles.singleOrNull { it.profileId == document.selectedProfileId } }

    suspend fun setSelectedControllerIdentity(
        identity: ControllerIdentityV1?,
    ): ControllerProfileRepositoryResultV1<ControllerIdentityV1?> = mutate { document ->
        Mutation.Success(document.copy(selectedControllerIdentity = identity), identity)
    }

    suspend fun findBestMatch(
        identity: ControllerIdentityV1,
    ): ControllerProfileRepositoryResultV1<ControllerProfileMatchResultV1> =
        loadSuccess { ControllerProfileMatcherV1.match(identity, it.profiles) }

    suspend fun resolve(
        identity: ControllerIdentityV1?,
    ): ControllerProfileRepositoryResultV1<ControllerProfileResolutionV1> = loadSuccess { document ->
        if (identity == null) ControllerProfileResolutionV1.Ineligible
        else when (val match = ControllerProfileMatcherV1.match(identity, document.profiles)) {
            ControllerProfileMatchResultV1.NoMatch -> ControllerProfileResolutionV1.StandardAndroidGamepadFallback
            else -> ControllerProfileResolutionV1.Custom(match)
        }
    }

    private suspend fun <T> loadSuccess(
        selector: (ControllerProfilesDocumentV1) -> T,
    ): ControllerProfileRepositoryResultV1<T> = when (val loaded = loadDocument()) {
        is ControllerProfilesLoadResultV1.Success -> ControllerProfileRepositoryResultV1.Success(selector(loaded.document))
        is ControllerProfilesLoadResultV1.UnsupportedVersion -> failure(ControllerProfileRepositoryErrorV1.UNSUPPORTED_SCHEMA)
        ControllerProfilesLoadResultV1.Corrupt -> failure(ControllerProfileRepositoryErrorV1.CORRUPT_STORAGE)
        ControllerProfilesLoadResultV1.DuplicateProfileIds -> failure(ControllerProfileRepositoryErrorV1.DUPLICATE_STORED_IDS)
    }

    private suspend fun <T> mutate(
        mutation: (ControllerProfilesDocumentV1) -> Mutation<T>,
    ): ControllerProfileRepositoryResultV1<T> {
        var result: ControllerProfileRepositoryResultV1<T>? = null
        return try {
            store.update { raw ->
                val document = when (val decoded = ControllerProfilesCodecV1.decode(raw)) {
                    is ControllerProfilesLoadResultV1.Success -> decoded.document
                    is ControllerProfilesLoadResultV1.UnsupportedVersion -> {
                        result = failure(ControllerProfileRepositoryErrorV1.UNSUPPORTED_SCHEMA); return@update raw
                    }
                    ControllerProfilesLoadResultV1.Corrupt -> {
                        result = failure(ControllerProfileRepositoryErrorV1.CORRUPT_STORAGE); return@update raw
                    }
                    ControllerProfilesLoadResultV1.DuplicateProfileIds -> {
                        result = failure(ControllerProfileRepositoryErrorV1.DUPLICATE_STORED_IDS); return@update raw
                    }
                }
                when (val changed = mutation(document)) {
                    is Mutation.Failure -> {
                        result = failure(changed.error)
                        raw
                    }
                    is Mutation.Success -> {
                        result = ControllerProfileRepositoryResultV1.Success(changed.value)
                        ControllerProfilesCodecV1.encode(changed.document)
                    }
                }
            }
            result ?: failure(ControllerProfileRepositoryErrorV1.STORAGE_FAILURE)
        } catch (_: Exception) {
            failure(ControllerProfileRepositoryErrorV1.STORAGE_FAILURE)
        }
    }

    private sealed interface Mutation<out T> {
        data class Success<T>(val document: ControllerProfilesDocumentV1, val value: T) : Mutation<T>
        data class Failure(val error: ControllerProfileRepositoryErrorV1) : Mutation<Nothing>
    }

    private fun failure(error: ControllerProfileRepositoryErrorV1) =
        ControllerProfileRepositoryResultV1.Failure(error)
}

@Serializable
private data class DocumentDto(
    val schemaVersion: Int,
    val profiles: List<ProfileDto>,
    val selectedProfileId: String?,
    val selectedControllerIdentity: IdentityDto?,
)

@Serializable
private data class ProfileDto(
    val schemaVersion: Int,
    val profileId: String,
    val profileName: String,
    val identity: IdentityDto?,
    val buttonBindings: List<ButtonDto>,
    val axisBindings: List<AxisDto>,
    val axisAlternatives: List<AlternativeDto>,
) {
    fun toDomain() = ControllerProfileV1(
        schemaVersion, profileId, profileName, identity?.toDomain(),
        buttonBindings.map(ButtonDto::toDomain),
        axisBindings.map(AxisDto::toDomain),
        axisAlternatives.map(AlternativeDto::toDomain),
    )

    companion object {
        fun fromDomain(profile: ControllerProfileV1) = ProfileDto(
            profile.schemaVersion,
            profile.profileId,
            profile.profileName,
            profile.identity?.let(IdentityDto::fromDomain),
            profile.buttonBindings.sortedWith(compareBy({ it.keyCode }, { it.target.name })).map(ButtonDto::fromDomain),
            profile.axisBindings.canonical().map(AxisDto::fromDomain),
            profile.axisAlternatives.sortedWith(compareBy({ it.choiceGroup }, { it.priority }, { it.variantId }))
                .map(AlternativeDto::fromDomain),
        )
    }
}

@Serializable
private data class IdentityDto(
    val descriptor: String,
    val vendorId: Int,
    val productId: Int,
    val normalizedName: String,
    val sourceClass: String,
    val motionSignature: String,
) {
    fun toDomain() = ControllerIdentityV1(
        descriptor, vendorId, productId, normalizedName,
        ControllerSourceClassV1.valueOf(sourceClass), motionSignature,
    )
    companion object {
        fun fromDomain(identity: ControllerIdentityV1) = IdentityDto(
            identity.descriptor, identity.vendorId, identity.productId, identity.normalizedName,
            identity.sourceClass.name, identity.motionSignature,
        )
    }
}

@Serializable
private data class ButtonDto(val keyCode: Int, val target: String) {
    fun toDomain() = ButtonBindingV1(keyCode, N64SemanticTargetV1.valueOf(target))
    companion object {
        fun fromDomain(binding: ButtonBindingV1) = ButtonDto(binding.keyCode, binding.target.name)
    }
}

@Serializable
private data class AxisDto(
    val axisCode: Int,
    val direction: String,
    val target: String,
    val inverted: Boolean,
    val deadZone: Float?,
    val scale: Float,
    val pressThreshold: Float?,
    val releaseThreshold: Float?,
) {
    fun toDomain() = AxisBindingV1(
        axisCode, AxisDirectionV1.valueOf(direction), N64SemanticTargetV1.valueOf(target),
        inverted, deadZone, scale, pressThreshold, releaseThreshold,
    )
    companion object {
        fun fromDomain(binding: AxisBindingV1) = AxisDto(
            binding.axisCode, binding.direction.name, binding.target.name, binding.inverted,
            binding.deadZone, binding.scale, binding.pressThreshold, binding.releaseThreshold,
        )
    }
}

@Serializable
private data class AlternativeDto(
    val choiceGroup: String,
    val variantId: String,
    val priority: Int,
    val requiredAxes: List<Int>,
    val bindings: List<AxisDto>,
) {
    fun toDomain() = AxisBindingAlternativeV1(
        choiceGroup, variantId, priority, requiredAxes.toSet(), bindings.map(AxisDto::toDomain),
    )
    companion object {
        fun fromDomain(alternative: AxisBindingAlternativeV1) = AlternativeDto(
            alternative.choiceGroup, alternative.variantId, alternative.priority,
            alternative.requiredAxes.sorted(), alternative.bindings.canonical().map(AxisDto::fromDomain),
        )
    }
}

private fun List<AxisBindingV1>.canonical() = sortedWith(
    compareBy<AxisBindingV1>({ it.axisCode }, { it.direction.name }, { it.target.name }),
)
