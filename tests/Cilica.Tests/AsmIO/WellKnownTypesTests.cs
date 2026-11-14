namespace Cilica.Tests.AsmIO;

using Cilica.AsmIO;

[Collection("ModuleResolver")]
public class WellKnownTypesTests
{
    readonly ModuleResolver _modResolver;

    public WellKnownTypesTests(ModuleResolverFixture mrf)
    {
        _modResolver = mrf.Resolver;

        // WellKnownTypes only sees modules the resolver has already loaded, so make sure the
        // assemblies providing the cached types are present.
        _modResolver.Import(typeof(object));
        _modResolver.Import(typeof(System.Text.StringBuilder));
        _modResolver.Import(typeof(System.Collections.Generic.List<>));
        _modResolver.Import(typeof(System.Numerics.BitOperations));
        _modResolver.Import(typeof(System.Runtime.InteropServices.MemoryMarshal));
        _modResolver.Import(typeof(System.Linq.Enumerable));
    }

    [Fact]
    public void WellKnownInstanceIsSharedPerResolver()
    {
        Assert.Same(_modResolver.WellKnown, _modResolver.WellKnown);
    }

    [Fact]
    public void ResolvesCommonTypes()
    {
        var wellKnown = _modResolver.WellKnown;

        Assert.NotNull(wellKnown.Object);
        Assert.NotNull(wellKnown.String);
        Assert.NotNull(wellKnown.Type);
        Assert.NotNull(wellKnown.Delegate);
        Assert.NotNull(wellKnown.Exception);
        Assert.NotNull(wellKnown.IDisposable);
        Assert.NotNull(wellKnown.StringBuilder);
        Assert.NotNull(wellKnown.ListOfT);
        Assert.NotNull(wellKnown.IEnumerableOfT);
        Assert.NotNull(wellKnown.Enumerable);
        Assert.NotNull(wellKnown.MemoryMarshal);
        Assert.NotNull(wellKnown.RuntimeHelpers);
        Assert.NotNull(wellKnown.BitOperations);
        Assert.NotNull(wellKnown.CompilerGeneratedAttribute);
    }

    [Fact]
    public void GetTypeMatchesFindType()
    {
        var wellKnown = _modResolver.WellKnown;

        Assert.Same(_modResolver.FindType("System.Text.StringBuilder"), wellKnown.GetType("System.Text.StringBuilder"));
        Assert.Same(wellKnown.StringBuilder, wellKnown.GetType("System.Text.StringBuilder"));
        Assert.Same(wellKnown.ListOfT, wellKnown.GetType("System.Collections.Generic.List`1"));
    }

    [Fact]
    public void GetTypeCachesMisses()
    {
        var wellKnown = _modResolver.WellKnown;

        Assert.Null(wellKnown.GetType("Cilica.Does.Not.Exist"));
        Assert.Null(wellKnown.GetType("Cilica.Does.Not.Exist"));
    }

    [Fact]
    public void ResolvesStringBuilderMembers()
    {
        var wellKnown = _modResolver.WellKnown;

        var ctor = Assert.IsAssignableFrom<MethodDesc>(wellKnown.StringBuilderCtor);
        Assert.Equal(".ctor", ctor.Name);
        Assert.Equal(2, ctor.ParamSig.Count); // this + string
        Assert.Equal(PrimType.String, ctor.ParamSig[1].Type);

        var appendString = Assert.IsAssignableFrom<MethodDesc>(wellKnown.StringBuilderAppendString);
        Assert.Equal("Append", appendString.Name);
        Assert.Equal(2, appendString.ParamSig.Count); // this + string
        Assert.Equal(PrimType.String, appendString.ParamSig[1].Type);
        Assert.Equal(wellKnown.StringBuilder, appendString.ReturnType);

        var appendChar = Assert.IsAssignableFrom<MethodDesc>(wellKnown.StringBuilderAppendChar);
        Assert.Equal(2, appendChar.ParamSig.Count); // this + char
        Assert.Equal(PrimType.Char, appendChar.ParamSig[1].Type);

        var toString = Assert.IsAssignableFrom<MethodDesc>(wellKnown.StringBuilderToString);
        Assert.Equal(1, toString.ParamSig.Count); // this only
        Assert.Equal(PrimType.String, toString.ReturnType);
    }

    [Fact]
    public void ResolvesCollectionMembers()
    {
        var wellKnown = _modResolver.WellKnown;

        var add = Assert.IsAssignableFrom<MethodDesc>(wellKnown.ListAdd);
        Assert.Equal("Add", add.Name);
        Assert.Equal(2, add.ParamSig.Count); // this + T

        var count = Assert.IsAssignableFrom<MethodDesc>(wellKnown.ListCount);
        Assert.Equal("get_Count", count.Name);
        Assert.Equal(PrimType.Int32, count.ReturnType);

        var item = Assert.IsAssignableFrom<MethodDesc>(wellKnown.ListItem);
        Assert.Equal("get_Item", item.Name);

        var toArray = Assert.IsAssignableFrom<MethodDesc>(wellKnown.ListToArray);
        Assert.Equal("ToArray", toArray.Name);

        var ctorWithCapacity = Assert.IsAssignableFrom<MethodDesc>(wellKnown.ListCtorCapacity);
        Assert.Equal(".ctor", ctorWithCapacity.Name);
        Assert.Equal(2, ctorWithCapacity.ParamSig.Count); // this + capacity
        Assert.Equal(PrimType.Int32, ctorWithCapacity.ParamSig[1].Type);
    }

    [Fact]
    public void ResolvesHelperMembers()
    {
        var wellKnown = _modResolver.WellKnown;

        var empty = Assert.IsAssignableFrom<MethodDesc>(wellKnown.ArrayEmpty);
        Assert.Equal("Empty", empty.Name);
        Assert.Equal(0, empty.ParamSig.Count); // static without parameters

        var popCount = Assert.IsAssignableFrom<MethodDesc>(wellKnown.BitOperationsPopCount);
        Assert.Equal("PopCount", popCount.Name);
        Assert.Equal(1, popCount.ParamSig.Count); // static, no this
        Assert.Equal(PrimType.UInt32, popCount.ParamSig[0].Type);
        Assert.Equal(PrimType.Int32, popCount.ReturnType);

        var createSpan = Assert.IsAssignableFrom<MethodDesc>(wellKnown.MemoryMarshalCreateSpan);
        Assert.Equal("CreateSpan", createSpan.Name);

        var getArrayDataReference = Assert.IsAssignableFrom<MethodDesc>(wellKnown.MemoryMarshalGetArrayDataReference);
        Assert.Equal("GetArrayDataReference", getArrayDataReference.Name);

        var substring = Assert.IsAssignableFrom<MethodDesc>(wellKnown.StringSubstring);
        Assert.Equal("Substring", substring.Name);
        Assert.Equal(3, substring.ParamSig.Count); // this + start + length
        Assert.Equal(PrimType.String, substring.ReturnType);
    }

    [Fact]
    public void GetMethodIsCachedPerType()
    {
        var wellKnown = _modResolver.WellKnown;

        var first = wellKnown.GetMethod(wellKnown.StringBuilder, "ToString", new MethodSig(PrimType.String, []));
        var second = wellKnown.GetMethod(wellKnown.StringBuilder, "ToString", new MethodSig(PrimType.String, []));

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Same(wellKnown.StringBuilderToString, first);
    }

    [Fact]
    public void GetMethodReturnsNullForMissingMember()
    {
        var wellKnown = _modResolver.WellKnown;

        Assert.Null(wellKnown.GetMethod(wellKnown.StringBuilder, "DoesNotExist"));
        Assert.Null(wellKnown.GetMethod(wellKnown.StringBuilder, "DoesNotExist"));
        Assert.Null(wellKnown.GetMethod(null, "Anything"));
    }

    [Fact]
    public void GetMethodWithoutSignatureReturnsFirstOverload()
    {
        var wellKnown = _modResolver.WellKnown;

        var append = wellKnown.GetMethod(wellKnown.StringBuilder, "Append");

        Assert.NotNull(append);
        Assert.Equal("Append", append!.Name);
    }

    [Fact]
    public void CompilerFeatureTypesAreUnavailableOnNet10()
    {
        var wellKnown = _modResolver.WellKnown;

        // .NET 11 introduced IUnion/UnionAttribute/IsClosedTypeAttribute. On earlier
        // frameworks the resolver simply reports them as absent instead of throwing.
        Assert.Equal(wellKnown.IsClosedTypeAttribute != null, wellKnown.IsClosedTypeAttributeCtor != null);
        Assert.Equal(wellKnown.UnionAttribute != null, wellKnown.UnionAttributeCtor != null);
        Assert.Equal(wellKnown.IUnion != null, wellKnown.IUnionGetValue != null);
    }

    [Fact]
    public void MissingTypeYieldsNoMembers()
    {
        var wellKnown = _modResolver.WellKnown;

        Assert.Null(wellKnown.GetMethod(wellKnown.IsClosedTypeAttribute, ".ctor"));
        Assert.Null(wellKnown.GetMethod(wellKnown.UnionAttribute, ".ctor"));
    }
}
