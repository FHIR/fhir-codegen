// <copyright file="ConceptMapExtensions.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text;
using Hl7.Fhir.Model;
using Fhir.CodeGen.Common.FhirExtensions;

namespace Fhir.CodeGen.Lib.FhirExtensions;

public static class ConceptMapExtensions
{
    public static string? cgSourceScope(this ConceptMap cm)
    {
        if ((cm.SourceScope is Canonical canonical) && (!string.IsNullOrEmpty(canonical.Uri)))
        {
            return canonical.Uri;
        }

        if ((cm.SourceScope is FhirUri uri) && (!string.IsNullOrEmpty(uri.Value)))
        {
            return uri.Value;
        }

        return null;
    }

    public static string? cgTargetScope(this ConceptMap cm)
    {
        if ((cm.TargetScope is Canonical canonical) && (!string.IsNullOrEmpty(canonical.Uri)))
        {
            return canonical.Uri;
        }

        if ((cm.TargetScope is FhirUri uri) && (!string.IsNullOrEmpty(uri.Value)))
        {
            return uri.Value;
        }

        return null;
    }

    /// <summary>
    /// Represents a mapping between source and target elements in a ConceptMap.
    /// </summary>
    public record class ConceptMapElementMapping
    {
        /// <summary>
        /// Gets or sets the source system URI.
        /// </summary>
        public required string? SourceSystem { get; init; }

        /// <summary>
        /// Gets or sets the target system URI.
        /// </summary>
        public required string? TargetSystem { get; init; }

        /// <summary>
        /// Gets or sets the source element component.
        /// </summary>
        public required ConceptMap.SourceElementComponent SourceElement { get; init; }

        /// <summary>
        /// Gets or sets the target element component.
        /// </summary>
        public required ConceptMap.TargetElementComponent? TargetElement { get; init; }

        public string SourceKey => (SourceSystem ?? string.Empty) + "#" + SourceElement.Code;
        public string? TargetKey => (TargetSystem == null) && (TargetElement == null)
            ? null
            : (TargetSystem ?? string.Empty) + "#" + (TargetElement?.Code ?? string.Empty);
    }

    public static IEnumerable<ConceptMapElementMapping> cgGetMappings(this ConceptMap cm)
    {
        if ((cm.Group == null) || (cm.Group.Count == 0))
        {
            yield break;
        }

        // iterate across groups
        foreach (ConceptMap.GroupComponent group in cm.Group)
        {
            // grab the source and target
            string? sourceSystem = group.Source;
            string? targetSystem = group.Target;

            // iterate across each element in the group
            foreach (ConceptMap.SourceElementComponent sourceElement in group.Element)
            {
                // add if this is a no map
                if (sourceElement.NoMap == true)
                {
                    yield return new()
                    {
                        SourceSystem = sourceSystem,
                        TargetSystem = targetSystem,
                        SourceElement = sourceElement,
                        TargetElement = null,
                    };

                    continue;
                }

                // iterate across each target element
                foreach (ConceptMap.TargetElementComponent targetElement in sourceElement.Target)
                {
                    yield return new()
                    {
                        SourceSystem = sourceSystem,
                        TargetSystem = targetSystem,
                        SourceElement = sourceElement,
                        TargetElement = targetElement,
                    };
                }
            }
        }

        yield break;
    }
}
