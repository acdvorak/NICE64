package com.melfegor.nice64.api

import com.melfegor.nice64.core.input.AxisBindingAlternativeV1
import com.melfegor.nice64.core.input.AxisBindingV1
import com.melfegor.nice64.core.input.ButtonBindingV1
import com.melfegor.nice64.core.input.ControllerProfileV1

/** Order-insensitive comparison of the semantic profile fields persisted by P3. */
internal data class ControllerProfileSemanticComparison(
    val equivalent: Boolean,
    val differingFields: List<String>,
)

internal fun compareControllerProfilesSemantically(
    requested: ControllerProfileV1,
    authoritative: ControllerProfileV1,
): ControllerProfileSemanticComparison {
    val differences = buildList {
        if (requested.schemaVersion != authoritative.schemaVersion) add("schemaVersion")
        if (requested.profileId != authoritative.profileId) add("profileId")
        if (requested.profileName != authoritative.profileName) add("profileName")
        if (requested.identity != authoritative.identity) add("identity")
        if (requested.buttonBindings.canonicalButtons() != authoritative.buttonBindings.canonicalButtons()) {
            add("buttonBindings")
        }
        if (requested.axisBindings.canonicalAxes() != authoritative.axisBindings.canonicalAxes()) {
            add("axisBindings")
        }
        if (requested.axisAlternatives.canonicalAlternatives() !=
            authoritative.axisAlternatives.canonicalAlternatives()
        ) {
            add("axisAlternatives")
        }
    }
    return ControllerProfileSemanticComparison(differences.isEmpty(), differences)
}

private fun List<ButtonBindingV1>.canonicalButtons(): List<ButtonBindingV1> =
    sortedWith(compareBy<ButtonBindingV1>({ it.keyCode }, { it.target.name }))

private fun List<AxisBindingV1>.canonicalAxes(): List<AxisBindingV1> =
    sortedWith(compareBy<AxisBindingV1>({ it.axisCode }, { it.direction.name }, { it.target.name }))

private fun List<AxisBindingAlternativeV1>.canonicalAlternatives(): List<AxisBindingAlternativeV1> =
    map { alternative ->
        alternative.copy(
            requiredAxes = alternative.requiredAxes.toSortedSet(),
            bindings = alternative.bindings.canonicalAxes(),
        )
    }.sortedWith(compareBy<AxisBindingAlternativeV1>(
        { it.choiceGroup },
        { it.priority },
        { it.variantId },
        { it.requiredAxes.joinToString(",") },
    ))
