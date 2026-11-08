using IAM.Modules.Identity.Application.Attributes;
using IAM.Modules.Identity.Domain.Attributes;
using Shouldly;

namespace IAM.Persistence.Tests.Identity;

/// <summary>S1-08 attribute catalog rules (IAM-WF-013/014/019, DL-006, DL-007). No database.</summary>
public sealed class AttributeCatalogDomainTests
{
    internal static AttributeDraft EeSubgroupCode(int maxLength = 8, AttributeNormalization normalization = AttributeNormalization.None) => new(
        "Employee subgroup code",
        AttributeDataType.Code,
        AttributeSubjectScope.Beneficiary,
        "successfactors",
        AttributeSensitivity.Internal,
        normalization,
        1440,
        maxLength,
        [AttributeOperator.Equals, AttributeOperator.In, AttributeOperator.NotIn, AttributeOperator.IsEmpty]);

    internal static AttributeDraftRequest EeSubgroupCodeRequest() => new(
        "Employee subgroup code", "Code", "Beneficiary", "successfactors", "Internal", "None", 1440, 8, ["Equals", "In", "NotIn", "IsEmpty"]);

    // ---- Registration and drafts --------------------------------------------------------------------------------

    [Fact]
    public void RegisteringCreatesDraftVersionOne()
    {
        var definition = AttributeDefinition.Register("ee_subgroup_code", EeSubgroupCode());

        definition.Id.ShouldNotBe(Guid.Empty);
        definition.Key.ShouldBe("ee_subgroup_code");
        var draft = definition.CurrentDraft.ShouldNotBeNull();
        draft.VersionNumber.ShouldBe(1);
        draft.State.ShouldBe(AttributeVersionState.Draft);
        draft.AttributeDefinitionId.ShouldBe(definition.Id);
        draft.OperatorList.ShouldBe([AttributeOperator.Equals, AttributeOperator.In, AttributeOperator.NotIn, AttributeOperator.IsEmpty]);
        draft.ContentHash.ShouldBe(AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("EeSubgroupCode")]
    [InlineData("1code")]
    [InlineData("ee-subgroup")]
    [InlineData("ee subgroup")]
    [InlineData("ee_subgroup_code_that_is_far_too_long_for_the_catalog_key_limit_xx")]
    public void InvalidKeysAreRejected(string? key) =>
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeDefinition.Register(key!, EeSubgroupCode())).Field.ShouldBe("Key");

    [Fact]
    public void PublishingIsLockedUntilMakerCheckerExists()
    {
        var definition = AttributeDefinition.Register("ee_subgroup_code", EeSubgroupCode());
        Should.Throw<AttributeActivationLockedException>(definition.Publish).Message.ShouldContain("DL-007");
        definition.CurrentDraft!.State.ShouldBe(AttributeVersionState.Draft);
    }

    [Fact]
    public void UpdatingADraftChangesItsContentAndHash()
    {
        var definition = AttributeDefinition.Register("ee_subgroup_code", EeSubgroupCode());
        var before = definition.CurrentDraft!.ContentHash;

        definition.UpdateDraft(EeSubgroupCode() with { Operators = [AttributeOperator.Equals, AttributeOperator.NotEquals] }).ShouldBeTrue();
        definition.CurrentDraft!.OperatorList.ShouldBe([AttributeOperator.Equals, AttributeOperator.NotEquals]);
        definition.CurrentDraft.ContentHash.ShouldNotBe(before);

        definition.UpdateDraft(EeSubgroupCode() with { Operators = [AttributeOperator.NotEquals, AttributeOperator.Equals] })
            .ShouldBeFalse("operator order does not change the content");
    }

    [Fact]
    public void TheContentHashIsDeterministicAndCoversEveryField()
    {
        var baseline = AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode());
        AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode()).ShouldBe(baseline);
        baseline.ShouldMatch("^[0-9a-f]{64}$");

        var variants = new[]
        {
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup", 1, EeSubgroupCode()),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 2, EeSubgroupCode()),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode() with { DisplayName = "Other" }),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode() with { DataType = AttributeDataType.Text }),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode() with { SubjectScope = AttributeSubjectScope.Requester }),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode() with { SourceSystem = "sap_hr" }),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode() with { Sensitivity = AttributeSensitivity.Restricted }),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode(normalization: AttributeNormalization.Trim)),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode() with { FreshnessMaxAgeMinutes = 60 }),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode(maxLength: 9)),
            AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode() with { Operators = [AttributeOperator.Equals] }),
        };

        variants.ShouldAllBe(v => v != baseline);
    }

    // ---- Field rules --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" padded")]
    [InlineData("line\nbreak")]
    [InlineData("bidi\u202Eoverride")]
    public void BadDisplayNamesAreRejected(string displayName) =>
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { DisplayName = displayName })).Field.ShouldBe("DisplayName");

    [Theory]
    [InlineData("SuccessFactors")]
    [InlineData("")]
    [InlineData("sap-hr")]
    public void SourceSystemsMustBeCodes(string source) =>
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { SourceSystem = source })).Field.ShouldBe("SourceSystem");

    [Theory]
    [InlineData(0)]
    [InlineData(525_601)]
    public void FreshnessIsBounded(int minutes) =>
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { FreshnessMaxAgeMinutes = minutes })).Field.ShouldBe("FreshnessMaxAgeMinutes");

    [Fact]
    public void MaxLengthAndNormalizationApplyOnlyToCodesAndText()
    {
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { MaxLength = null })).Field.ShouldBe("MaxLength");
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode(maxLength: 401))).Field.ShouldBe("MaxLength");

        var grade = EeSubgroupCode() with { DataType = AttributeDataType.Integer, MaxLength = null, Operators = [AttributeOperator.GreaterThanOrEqual] };
        AttributeRules.ValidateDraft(grade);
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(grade with { MaxLength = 3 })).Field.ShouldBe("MaxLength");
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(grade with { Normalization = AttributeNormalization.Trim })).Field.ShouldBe("Normalization");
    }

    [Theory]
    [InlineData(AttributeDataType.Code, AttributeOperator.GreaterThan)]
    [InlineData(AttributeDataType.Text, AttributeOperator.LessThanOrEqual)]
    [InlineData(AttributeDataType.Boolean, AttributeOperator.In)]
    [InlineData(AttributeDataType.Boolean, AttributeOperator.GreaterThan)]
    public void OperatorsMustFitTheDataType(AttributeDataType type, AttributeOperator op)
    {
        var draft = EeSubgroupCode() with
        {
            DataType = type,
            MaxLength = AttributeRules.IsStringType(type) ? 8 : null,
            Operators = [AttributeOperator.Equals, op],
        };
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(draft)).Field.ShouldBe("Operators");
    }

    [Fact]
    public void OperatorListsMustBeNonEmptyDefinedAndDistinct()
    {
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { Operators = [] }));
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { Operators = [AttributeOperator.In, AttributeOperator.In] }));
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { Operators = [(AttributeOperator)99] }));
    }

    [Fact]
    public void OrderedTypesAllowRangeOperators()
    {
        foreach (var type in new[] { AttributeDataType.Integer, AttributeDataType.Decimal, AttributeDataType.Date, AttributeDataType.Timestamp })
        {
            AttributeRules.AllowedOperators(type).ShouldContain(AttributeOperator.GreaterThanOrEqual);
        }

        AttributeRules.AllowedOperators(AttributeDataType.Code).ShouldNotContain(AttributeOperator.GreaterThan);
    }

    // ---- Values (DL-006) ----------------------------------------------------------------------------------------

    [Fact]
    public void CodesKeepLeadingZerosAndAreNeverNumbers()
    {
        var code = EeSubgroupCode();
        var zeroOne = AttributeValueValidator.Validate(code, "01");
        var one = AttributeValueValidator.Validate(code, "1");

        zeroOne.IsValid.ShouldBeTrue();
        zeroOne.Value.ShouldBe("01");
        zeroOne.Canonical.ShouldBe("01");
        one.Canonical.ShouldBe("1");
        zeroOne.Canonical.ShouldNotBe(one.Canonical, "\"01\" and \"1\" are different codes (DL-006)");
        AttributeValueValidator.Validate(code, "0100").Value.ShouldBe("0100");
    }

    [Theory]
    [InlineData(" 01")]
    [InlineData("01 ")]
    [InlineData("")]
    [InlineData("0 1")]
    [InlineData("123456789")]
    [InlineData("01;DROP")]
    [InlineData("<b>")]
    public void InvalidCodesAreRejectedWithoutNormalization(string raw) =>
        AttributeValueValidator.Validate(EeSubgroupCode(), raw).IsValid.ShouldBeFalse();

    [Fact]
    public void DeclaredNormalizationIsTheOnlyChange()
    {
        AttributeValueValidator.Validate(EeSubgroupCode(normalization: AttributeNormalization.Trim), " 01 ").Value.ShouldBe("01");
        AttributeValueValidator.Validate(EeSubgroupCode(normalization: AttributeNormalization.TrimUpperInvariant), " cc1001 ").Value.ShouldBe("CC1001");
        AttributeValueValidator.Validate(EeSubgroupCode(), "cc1001").Value.ShouldBe("cc1001", "no normalization means exact");
    }

    [Theory]
    [InlineData(AttributeDataType.Integer, "7", true, "7")]
    [InlineData(AttributeDataType.Integer, "07", true, "7")]
    [InlineData(AttributeDataType.Integer, "-12", true, "-12")]
    [InlineData(AttributeDataType.Integer, "1,000", false, null)]
    [InlineData(AttributeDataType.Integer, "7.0", false, null)]
    [InlineData(AttributeDataType.Integer, "9999999999999999999", false, null)]
    [InlineData(AttributeDataType.Decimal, "12.5", true, "12.5")]
    [InlineData(AttributeDataType.Decimal, "1e3", false, null)]
    [InlineData(AttributeDataType.Decimal, "0.123456789", false, null)]
    [InlineData(AttributeDataType.Date, "2026-11-08", true, "2026-11-08")]
    [InlineData(AttributeDataType.Date, "08/11/2026", false, null)]
    [InlineData(AttributeDataType.Date, "2026-02-30", false, null)]
    [InlineData(AttributeDataType.Boolean, "true", true, "true")]
    [InlineData(AttributeDataType.Boolean, "True", false, null)]
    [InlineData(AttributeDataType.Boolean, "1", false, null)]
    [InlineData(AttributeDataType.Timestamp, "2026-11-08T10:00:00Z", true, "2026-11-08T10:00:00.000000Z")]
    [InlineData(AttributeDataType.Timestamp, "2026-11-08T10:00:00.123456Z", true, "2026-11-08T10:00:00.123456Z")]
    [InlineData(AttributeDataType.Timestamp, "2026-11-08T10:00:00+02:00", false, null)]
    [InlineData(AttributeDataType.Timestamp, "2026-11-08 10:00:00", false, null)]
    public void TypedValuesAreParsedStrictly(AttributeDataType type, string raw, bool valid, string? canonical)
    {
        var draft = EeSubgroupCode() with { DataType = type, MaxLength = null, Operators = [AttributeOperator.Equals] };
        var result = AttributeValueValidator.Validate(draft, raw);
        result.IsValid.ShouldBe(valid);
        result.Canonical.ShouldBe(canonical);
    }

    [Fact]
    public void TextRejectsControlAndInvisibleCharacters()
    {
        var text = EeSubgroupCode() with { DataType = AttributeDataType.Text, MaxLength = 20 };
        AttributeValueValidator.Validate(text, "Finance & Control").IsValid.ShouldBeTrue("ordinary punctuation is data, not an attack");
        AttributeValueValidator.Validate(text, "a\u0000b").IsValid.ShouldBeFalse();
        AttributeValueValidator.Validate(text, "a\u200Bb").IsValid.ShouldBeFalse();
        AttributeValueValidator.Validate(text, new string('x', 21)).IsValid.ShouldBeFalse();
        AttributeValueValidator.Validate(text, null).IsValid.ShouldBeFalse();
    }

    // ---- Request mapping (BACKEND_INPUT_VALIDATION) ------------------------------------------------------------

    [Fact]
    public void AValidRequestMapsToTheDraft() =>
        AttributeRequestMapper.ToDraft(EeSubgroupCodeRequest()).ShouldBe(
            EeSubgroupCode(),
            new DraftComparer());

    [Theory]
    [InlineData("code")]
    [InlineData("CODE")]
    [InlineData("1")]
    [InlineData("Code,Text")]
    [InlineData(" Code")]
    [InlineData("Unknown")]
    [InlineData(null)]
    public void EnumValuesMustBeExactNames(string? dataType) =>
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRequestMapper.ToDraft(EeSubgroupCodeRequest() with { DataType = dataType })).Field.ShouldBe("DataType");

    [Fact]
    public void MissingAndOversizedRequestPartsAreRejected()
    {
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRequestMapper.ToDraft(null));
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRequestMapper.ToDraft(EeSubgroupCodeRequest() with { Operators = null })).Field.ShouldBe("Operators");
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRequestMapper.ToDraft(EeSubgroupCodeRequest() with { Operators = ["Equals", "Exec"] })).Field.ShouldBe("Operators");
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRequestMapper.ToDraft(EeSubgroupCodeRequest() with
        {
            Operators = Enumerable.Repeat<string?>("Equals", 11).ToList(),
        })).Field.ShouldBe("Operators");
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRequestMapper.ToDraft(EeSubgroupCodeRequest() with { FreshnessMaxAgeMinutes = null })).Field.ShouldBe("FreshnessMaxAgeMinutes");
    }

    // ---- Review fixes: trailing line breaks, canonical values, bounds ---------------------------------------------

    [Theory]
    [InlineData("ee_code\n")]
    [InlineData("ee_code\r\n")]
    [InlineData("ee_code\r")]
    public void KeysWithTrailingLineBreaksAreRejected(string key) =>
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateKey(key)).Field.ShouldBe("Key");

    [Fact]
    public void SourceSystemsWithTrailingLineBreaksAreRejected() =>
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { SourceSystem = "sap_hr\n" })).Field.ShouldBe("SourceSystem");

    [Theory]
    [InlineData(AttributeDataType.Code, "01\n")]
    [InlineData(AttributeDataType.Code, "X1\r\n")]
    [InlineData(AttributeDataType.Code, "01\r")]
    [InlineData(AttributeDataType.Integer, "7\n")]
    [InlineData(AttributeDataType.Decimal, "1.5\n")]
    [InlineData(AttributeDataType.Timestamp, "2026-11-08T10:00:00Z\n")]
    [InlineData(AttributeDataType.Timestamp, "2026-12-31T23:59:60Z")]
    public void ValuesWithTrailingLineBreaksOrImpossibleTimesAreRejected(AttributeDataType type, string raw)
    {
        var draft = EeSubgroupCode() with { DataType = type, MaxLength = AttributeRules.IsStringType(type) ? 8 : null, Operators = [AttributeOperator.Equals] };
        AttributeValueValidator.Validate(draft, raw).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("12.5", "12.5")]
    [InlineData("12.50", "12.5")]
    [InlineData("12.500", "12.5")]
    [InlineData("100", "100")]
    [InlineData("100.0", "100")]
    [InlineData("0", "0")]
    [InlineData("0.00", "0")]
    [InlineData("-0.0", "0")]
    [InlineData("-1.25", "-1.25")]
    public void DecimalsHaveOneCanonicalForm(string raw, string canonical)
    {
        var draft = EeSubgroupCode() with { DataType = AttributeDataType.Decimal, MaxLength = null, Operators = [AttributeOperator.Equals] };
        AttributeValueValidator.Validate(draft, raw).Canonical.ShouldBe(canonical);
    }

    [Fact]
    public void EmptyTextIsNotAValue()
    {
        var text = EeSubgroupCode() with { DataType = AttributeDataType.Text, MaxLength = 20 };
        AttributeValueValidator.Validate(text, string.Empty).IsValid.ShouldBeFalse("a missing value must be absent, never an empty string");
        AttributeValueValidator.Validate(text with { Normalization = AttributeNormalization.Trim }, "   ").IsValid.ShouldBeFalse();
    }

    [Fact]
    public void RawInputIsBoundedBeforeNormalization()
    {
        var trimmed = EeSubgroupCode(normalization: AttributeNormalization.Trim);
        AttributeValueValidator.Validate(trimmed, new string(' ', AttributeValueValidator.MaxRawLength + 1) + "01").IsValid.ShouldBeFalse();
        AttributeValueValidator.Validate(trimmed, new string(' ', 100) + "01").Value.ShouldBe("01");
    }

    [Fact]
    public void DisplayNamesMustBeNfcWithoutPrivateUseOrUnassignedCharacters()
    {
        AttributeRules.ValidateDraft(EeSubgroupCode() with { DisplayName = "Caf" + (char)0x00E9 });
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { DisplayName = "Cafe" + (char)0x0301 }))
            .Field.ShouldBe("DisplayName");
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { DisplayName = "Code " + (char)0xE000 }))
            .Field.ShouldBe("DisplayName");
        Should.Throw<AttributeDefinitionRejectedException>(() => AttributeRules.ValidateDraft(EeSubgroupCode() with { DisplayName = "Code " + (char)0x0378 }))
            .Field.ShouldBe("DisplayName");
    }

    /// <summary>
    /// Fixed expected values, so the content-hash layout (ADR-0015) cannot drift silently between builds or .NET
    /// versions. An outside verifier can reproduce them from the documented canonical form.
    /// </summary>
    [Fact]
    public void TheContentHashHasFixedExpectedValues()
    {
        AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode()).ShouldBe("5d9148d94df29c5c23f88b1b2ae3b41e47c91321010993f38a5526903b6b487e");
        AttributeDefinitionVersion.ComputeContentHash("ee_subgroup_code", 1, EeSubgroupCode() with { DisplayName = "Zo" + (char)0x00EB + " <R&D> +1" })
            .ShouldBe("bbc8b1e99a1d504f636fd524e6f71a609c4f4b978c2c3edf7e556dde2056fe84");
    }

    private sealed class DraftComparer : IEqualityComparer<AttributeDraft>
    {
        public bool Equals(AttributeDraft? x, AttributeDraft? y) =>
            x is not null && y is not null
            && AttributeDefinitionVersion.ComputeContentHash("k_k", 1, x) == AttributeDefinitionVersion.ComputeContentHash("k_k", 1, y);

        public int GetHashCode(AttributeDraft obj) => 0;
    }
}
