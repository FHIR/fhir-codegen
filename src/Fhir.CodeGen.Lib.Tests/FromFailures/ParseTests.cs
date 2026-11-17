// <copyright file="StructureParseTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>


using System.Text.Json;
using Shouldly;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Tests.Extensions;
using Fhir.CodeGen.Common.Models;

namespace Fhir.CodeGen.Lib.Tests.FromFailures;

/// <summary>A structure parse tests.</summary>
public class ParseTests
{
    [Theory]
    [InlineData("snapshot", "@value|f:*|h:div")]
    [InlineData("differential", "not(exists(f:comparator))")]
    public void Dstu2ConstraintXPathIsRetainedOnConstraint(string projection, string xpath)
    {
        string json = $$"""
            {
              "resourceType": "StructureDefinition",
              "id": "SimpleQuantity",
              "url": "http://hl7.org/fhir/StructureDefinition/SimpleQuantity",
              "name": "SimpleQuantity",
              "status": "draft",
              "kind": "datatype",
              "abstract": false,
              "constrainedType": "Quantity",
              "base": "http://hl7.org/fhir/StructureDefinition/Quantity",
              "{{projection}}": {
                "element": [{
                  "path": "Quantity",
                  "min": 0,
                  "max": "*",
                  "constraint": [
                    {
                      "key": "with-xpath",
                      "severity": "error",
                      "human": "Preserve the original XPath.",
                      "xpath": "{{xpath}}"
                    },
                    {
                      "key": "without-xpath",
                      "severity": "warning",
                      "human": "No XPath was supplied."
                    }
                  ]
                }]
              }
            }
            """;

        const string extensionUrl = "http://hl7.org/fhir/1.0/StructureDefinition/extension-ElementDefinition.constraint.xpath";
        Fhir.CodeGen.CrossVersionLoader.Converter_20_50 converter = new();
        StructureDefinition definition = converter.Convert(FhirJsonNode.Parse(json)).ShouldBeOfType<StructureDefinition>();
        List<ElementDefinition> elements = projection == "snapshot"
            ? definition.Snapshot.Element : definition.Differential.Element;
        ElementDefinition element = elements.ShouldHaveSingleItem();
        element.Constraint.Count.ShouldBe(2);
        ElementDefinition.ConstraintComponent constraint = element.Constraint[0];
        Extension extension = constraint.Extension.ShouldHaveSingleItem();
        extension.Url.ShouldBe(extensionUrl);
        extension.Value.ShouldBeOfType<FhirString>().Value.ShouldBe(xpath);
        extension.Extension.ShouldBeEmpty();
        constraint.Expression.ShouldBeNull();
        element.Constraint[1].Extension.ShouldBeEmpty();
        element.Extension.ShouldNotContain(item => item.Url == extensionUrl);
        definition.Extension.ShouldNotContain(item => item.Url == extensionUrl);

        string serialized = new FhirJsonSerializer().SerializeToString(definition);
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement serializedConstraint = document.RootElement.GetProperty(projection)
            .GetProperty("element")[0].GetProperty("constraint")[0];
        serializedConstraint.GetProperty("extension")[0].GetProperty("valueString").GetString().ShouldBe(xpath);
        serializedConstraint.TryGetProperty("expression", out _).ShouldBeFalse();
        StructureDefinition roundTrip = new FhirJsonParser().Parse<StructureDefinition>(serialized);
        List<ElementDefinition> roundTripElements = projection == "snapshot"
            ? roundTrip.Snapshot.Element : roundTrip.Differential.Element;
        roundTripElements.ShouldHaveSingleItem().Constraint[0].Extension.ShouldHaveSingleItem()
            .Value.ShouldBeOfType<FhirString>().Value.ShouldBe(xpath);
    }

    ///// <summary>
    ///// Note that the Expansion is WRONG in several publications
    ///// TODO(ginoc): Remove this test when current build is verified correct.
    ///// </summary>
    ///// <param name="json">The JSON.</param>
    //[Theory]
    //[FileData("TestData/R5/expansions/ValueSet-units-of-time.json")]
    //[Trait("Category", "Issues")]
    //[Trait("FhirVersion", "R5")]
    //public void TestParseR5ValueSetUnitsOfTime(string json)
    //{
    //    FhirJsonPocoDeserializer parser = new(new FhirJsonPocoDeserializerSettings()
    //    {
    //        DisableBase64Decoding = false,
    //        Validator = null,
    //    });

    //    // always use lenient parsing
    //    Resource parsed = parser.DeserializeResource(json);

    //    parsed.ShouldNotBeNull();
    //    parsed.ShouldBeOfType<ValueSet>();

    //    ValueSet vs = (ValueSet)parsed;

    //    vs.Expansion.ShouldNotBeNull();
    //    vs.Expansion.Contains.ShouldNotBeEmpty();
    //    vs.Expansion.Contains.Count.ShouldBe(7);

    //    foreach (ValueSet.ContainsComponent cc in vs.Expansion.Contains)
    //    {
    //        cc.Display.ShouldStartWith(cc.Code);
    //    }
    //}

    [Theory]
    [FileData("TestData/R4B/ValueSet-nhin-purposeofuse.json")]
    [Trait("Category", "Issues")]
    [Trait("FhirVersion", "R4B")]
    public void TestParseR4BValueSetNhinPOU(string json)
    {
        Hl7.Fhir.ElementModel.ISourceNode sn = FhirJsonNode.Parse(json);

        Fhir.CodeGen.CrossVersionLoader.Converter_43_50 c = new();

        Resource r = c.Convert(sn);

        r.ShouldNotBeNull();
        r.ShouldBeOfType<ValueSet>();

        ValueSet vs = (ValueSet)r;

        vs.Id.ShouldBe("nhin-purposeofuse");
        vs.Name.ShouldBe("NHIN PurposeOfUse");
        vs.Contained.ShouldNotBeEmpty();
        vs.Contained[0].ShouldBeOfType<ConceptMap>();
        vs.Contained[0].Id.ShouldBe("map");

        vs.Experimental.ShouldBe(false);
        vs.DateElement.ShouldBeEquivalentTo(new FhirDateTime(2010, 1, 29));
    }

    [Theory]
    [FileData("TestData/R5/StructureDefinition-integer64.json")]
    [Trait("Category", "Issues")]
    [Trait("FhirVersion", "R5")]
    public void TestParseR5StructureInt64(string json)
    {
        FhirJsonPocoDeserializer parser = new(new FhirJsonPocoDeserializerSettings()
        {
            DisableBase64Decoding = false,
            Validator = null,
            OnPrimitiveParseFailed = LocalPrimitiveParseHandler,
        });

        // always use lenient parsing
        Resource parsed = parser.DeserializeResource(json);

        parsed.ShouldNotBeNull();
        parsed.ShouldBeOfType<StructureDefinition>();

        StructureDefinition sd = (StructureDefinition)parsed;

        sd.cgArtifactClass().ShouldBe(FhirArtifactClassEnum.PrimitiveType);
    }

    public (object?, FhirJsonException?) LocalPrimitiveParseHandler(
        ref Utf8JsonReader reader,
        Type targetType,
        object? originalValue,
        FhirJsonException originalException)
    {
        if (targetType == typeof(long))
        {
            if (originalValue is long ol)
            {
                return (ol, null);
            }

            if (originalValue is string s)
            {
                if (long.TryParse(s, out long l))
                {
                    return (l, null);
                }
            }
        }

        return (null, null);
    }
}
