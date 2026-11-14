namespace Cilica.Tests.AsmIO;

using System.Reflection;

using Cilica.AsmIO;

/// <summary>
/// Tests for the closed-class and union-type abstractions that C# 15 / .NET 11 introduced.
/// Because the test project targets .NET 10, the corresponding framework attributes and
/// interfaces do not exist, so they are synthesized into a dedicated module here.
/// </summary>
/// <remarks>
/// Every test class uses its own <see cref="ModuleResolver"/> so that the synthetic
/// <c>System.Runtime.CompilerServices.*</c> types do not leak into the shared fixture
/// (and therefore into <see cref="WellKnownTypes"/> of other tests).
/// </remarks>
public sealed class CompilerFeatureTypesFixture : IDisposable
{
    public ModuleResolver Resolver { get; }

    public ModuleDef Module { get; }

    public CompilerFeatureTypesFixture()
    {
        Resolver = new ModuleResolver();
        Resolver.AddTrustedSearchPaths();
        Module = Resolver.Create("SyntheticCompilerFeatures");
    }

    public void Dispose() => Resolver.Dispose();
}

public class ClosedAndUnionTypeTests : IClassFixture<CompilerFeatureTypesFixture>
{
    const string CompilerServicesNs = "System.Runtime.CompilerServices";

    readonly ModuleResolver _resolver;
    readonly ModuleDef _module;

    public ClosedAndUnionTypeTests(CompilerFeatureTypesFixture fixture)
    {
        _resolver = fixture.Resolver;
        _module = fixture.Module;
    }

    #region Helpers

    static MethodDef CreateParameterlessCtor(TypeDef type)
    {
        return type.CreateMethod(
            ".ctor", PrimType.Void,
            [new ParamDef(type, "this")],
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
        );
    }

    /// <summary>
    /// Creates an empty attribute class with the given name in the compiler-services namespace.
    /// The fixture module is shared by all tests of this class, so the factory is idempotent.
    /// </summary>
    TypeDef CreateAttribute(string name)
    {
        if (_module.FindType(CompilerServicesNs, name) is TypeDef existing) {
            return existing;
        }
        var type = _module.CreateType(
            CompilerServicesNs, name,
            TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
            _resolver.CoreLib.FindType("System", "Attribute")
        );
        CreateParameterlessCtor(type);
        return type;
    }

    TypeDef CreateAttributeInstance(string name, out MethodDesc ctor)
    {
        var type = CreateAttribute(name);
        ctor = type.FindMethod(".ctor", new MethodSig(PrimType.Void, []));
        return type;
    }

    TypeDef CreatePlainType(string name)
        => _module.CreateType(null, name + "_" + Guid.NewGuid().ToString("N"));

    #endregion

    #region IsClosed

    [Fact]
    public void IsClosedIsFalseByDefault()
    {
        var type = CreatePlainType(nameof(IsClosedIsFalseByDefault));

        Assert.False(type.IsClosed);
        Assert.Empty(type.GetCustomAttribs());
    }

    [Fact]
    public void SettingIsClosedAddsTheAttribute()
    {
        var attribType = CreateAttributeInstance("IsClosedTypeAttribute", out var attribCtor);
        var type = CreatePlainType(nameof(SettingIsClosedAddsTheAttribute));

        type.IsClosed = true;

        Assert.True(type.IsClosed);
        var attrib = Assert.Single(type.GetCustomAttribs());
        Assert.Equal(attribCtor, attrib.Constructor);
        Assert.Equal(attribType, attrib.Type);
    }

    [Fact]
    public void SettingIsClosedTwiceDoesNotDuplicateTheAttribute()
    {
        CreateAttribute("IsClosedTypeAttribute");
        var type = CreatePlainType(nameof(SettingIsClosedTwiceDoesNotDuplicateTheAttribute));

        type.IsClosed = true;
        type.IsClosed = true;

        Assert.True(type.IsClosed);
        Assert.Single(type.GetCustomAttribs());
    }

    [Fact]
    public void ClearingIsClosedRemovesTheAttribute()
    {
        CreateAttribute("IsClosedTypeAttribute");
        var type = CreatePlainType(nameof(ClearingIsClosedRemovesTheAttribute));

        type.IsClosed = true;
        type.IsClosed = false;

        Assert.False(type.IsClosed);
        Assert.Empty(type.GetCustomAttribs());
    }

    [Fact]
    public void ClearingIsClosedWhenUnsetIsANoOp()
    {
        CreateAttribute("IsClosedTypeAttribute");
        var type = CreatePlainType(nameof(ClearingIsClosedWhenUnsetIsANoOp));

        type.IsClosed = false;

        Assert.False(type.IsClosed);
        Assert.Empty(type.GetCustomAttribs());
    }

    [Fact]
    public void IsClosedDoesNotDisturbOtherAttributes()
    {
        CreateAttribute("IsClosedTypeAttribute");
        CreateAttributeInstance("UnrelatedAttribute", out var unrelatedCtor);
        var type = CreatePlainType(nameof(IsClosedDoesNotDisturbOtherAttributes));
        type.GetCustomAttribs(readOnly: false).Add(new CustomAttrib(unrelatedCtor));

        type.IsClosed = true;
        Assert.Equal(2, type.GetCustomAttribs().Count);

        type.IsClosed = false;
        var remaining = Assert.Single(type.GetCustomAttribs());
        Assert.Equal(unrelatedCtor, remaining.Constructor);
    }

    [Fact]
    public void SettingIsClosedWithoutTheFrameworkAttributeThrows()
    {
        // A dedicated resolver without the synthetic attribute: .NET 10 has no IsClosedTypeAttribute.
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Create("ClosedWithoutAttribute");
        var type = module.CreateType(null, "PlainType");

        Assert.False(type.IsClosed);
        var ex = Assert.Throws<InvalidOperationException>(() => type.IsClosed = true);
        Assert.Contains("IsClosedTypeAttribute", ex.Message);
        Assert.False(type.IsClosed);
    }

    #endregion

    #region Unions

    [Fact]
    public void PlainTypeIsNotAUnion()
    {
        var type = CreatePlainType(nameof(PlainTypeIsNotAUnion));

        Assert.False(type.IsUnion());
        Assert.Empty(type.GetUnionCases());
    }

    [Fact]
    public void UnionAttributeMarksATypeAsUnion()
    {
        var unionAttrib = CreateAttributeInstance("UnionAttribute", out var unionCtor);
        var union = CreatePlainType(nameof(UnionAttributeMarksATypeAsUnion));
        union.GetCustomAttribs(readOnly: false).Add(new CustomAttrib(unionCtor));

        Assert.True(union.IsUnion());
        Assert.Same(unionAttrib, union.GetCustomAttribs().Find(CompilerServicesNs, "UnionAttribute")!.Type);
    }

    [Fact]
    public void ForeignIUnionInterfaceDoesNotMarkATypeAsUnion()
    {
        // Only the corelib IUnion counts; a look-alike from a user assembly must not be mistaken for it.
        var iUnion = _module.FindType(CompilerServicesNs, "IUnion")
            ?? _module.CreateType(CompilerServicesNs, "IUnion", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        var union = CreatePlainType(nameof(ForeignIUnionInterfaceDoesNotMarkATypeAsUnion));
        union.Interfaces.Add(iUnion);

        Assert.False(union.IsUnion());
    }

    [Fact]
    public void UnionCasesAreDerivedFromConstructors()
    {
        CreateAttribute("UnionAttribute");
        var union = CreateUnion(nameof(UnionCasesAreDerivedFromConstructors), PrimType.String, PrimType.Int32);

        var cases = union.GetUnionCases();

        Assert.Equal(2, cases.Count);
        Assert.Contains(PrimType.String, cases);
        Assert.Contains(PrimType.Int32, cases);
        Assert.DoesNotContain(union, cases);
    }

    [Fact]
    public void UnionCasesAreDeduplicated()
    {
        CreateAttribute("UnionAttribute");
        var union = CreateUnion(nameof(UnionCasesAreDeduplicated), PrimType.String);

        // The same case is exposed through a second mechanism; it must not be reported twice.
        union.CreateMethod(
            "op_Implicit", union,
            [new ParamDef(PrimType.String, "value")],
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName
        );

        var cases = union.GetUnionCases();

        Assert.Single(cases);
        Assert.Equal(PrimType.String, cases[0]);
    }

    [Fact]
    public void UnionCasesIncludeImplicitConversions()
    {
        CreateAttribute("UnionAttribute");
        var union = CreatePlainType(nameof(UnionCasesIncludeImplicitConversions));
        var caseType = _module.CreateType(null, "CustomCase");

        union.CreateMethod(
            "op_Implicit", union,
            [new ParamDef(caseType, "value")],
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName
        );

        var cases = union.GetUnionCases();

        Assert.Single(cases);
        Assert.Equal(caseType, cases[0]);
    }

    [Fact]
    public void UnionCasesOfANonUnionAreEmpty()
    {
        var type = CreatePlainType(nameof(UnionCasesOfANonUnionAreEmpty));
        type.CreateMethod(
            ".ctor", PrimType.Void,
            [new ParamDef(type, "this"), new ParamDef(PrimType.String, "value")],
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
        );

        Assert.False(type.IsUnion());
        Assert.Empty(type.GetUnionCases());
    }

    [Fact]
    public void WellKnownTypesResolveSyntheticCompilerFeatures()
    {
        CreateAttributeInstance("UnionAttribute", out var unionCtor);
        CreateAttributeInstance("IsClosedTypeAttribute", out var closedCtor);

        var wellKnown = _resolver.WellKnown;

        Assert.NotNull(wellKnown.UnionAttribute);
        Assert.NotNull(wellKnown.IsClosedTypeAttribute);
        Assert.Equal(unionCtor, wellKnown.UnionAttributeCtor);
        Assert.Equal(closedCtor, wellKnown.IsClosedTypeAttributeCtor);
    }

    TypeDef CreateUnion(string name, params TypeDesc[] cases)
    {
        var unionAttribCtor = _module.FindType(CompilerServicesNs, "UnionAttribute")!.FindMethod(".ctor", new MethodSig(PrimType.Void, []));
        var union = CreatePlainType(name);
        union.GetCustomAttribs(readOnly: false).Add(new CustomAttrib(unionAttribCtor));

        foreach (var caseType in cases) {
            union.CreateMethod(
                ".ctor", PrimType.Void,
                [new ParamDef(union, "this"), new ParamDef(caseType, "value")],
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
            );
        }
        return union;
    }

    #endregion
}
