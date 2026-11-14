namespace Cilica.AsmIO;

/// <summary>
/// Caches references to commonly used framework types and members so that passes and analyses
/// do not have to resolve and cache them individually.
/// </summary>
/// <remarks>
/// Similar in spirit to Roslyn's <c>KnownTypeSymbols</c>. An instance is owned by a
/// <see cref="ModuleResolver"/> (see <see cref="ModuleResolver.WellKnown"/>) and is therefore
/// shared by every compilation created from that resolver.
/// <para>
/// Every member resolves lazily and returns <see langword="null"/> when the type or member does not
/// exist in the resolved framework. Consumers are expected to bail out gracefully instead of assuming
/// availability, so that a missing type degrades an optimization rather than failing a build.
/// Both hits and misses are cached, so repeated access is a dictionary lookup.
/// </para>
/// <para>
/// This helper exists for the members a pass intends to <em>emit</em>. To <em>classify</em> members
/// encountered in foreign IL, use <see cref="TypeUtils.IsCorelibType(TypeDesc, Type)"/> instead, which
/// only compares names and needs no resolution at all.
/// </para>
/// </remarks>
public sealed class WellKnownTypes
{
    readonly ModuleResolver _resolver;
    readonly Dictionary<string, TypeDefOrSpec?> _types = new(StringComparer.Ordinal);
    readonly Dictionary<MethodKey, MethodDesc?> _methods = new();

    internal WellKnownTypes(ModuleResolver resolver) => _resolver = resolver;

    /// <summary> Resolves a type by its full name, caching hits and misses. </summary>
    /// <remarks>
    /// Only types of modules that are already loaded can be found. The cache is therefore dropped
    /// whenever a module is added to the resolver, so that a miss never outlives the reason for it.
    /// </remarks>
    /// <returns> The type definition, or <see langword="null"/> if it could not be resolved. </returns>
    public TypeDefOrSpec? GetType(string fullName)
    {
        if (_types.TryGetValue(fullName, out var cached)) {
            return cached;
        }

        var type = _resolver.FindType(fullName) as TypeDefOrSpec;
        _types[fullName] = type;
        return type;
    }

    /// <summary> Resolves a method declared by <paramref name="type"/>, caching hits and misses. </summary>
    /// <param name="type"> The declaring type, or <see langword="null"/> to resolve nothing. </param>
    /// <param name="name"> The method name. </param>
    /// <param name="sig"> The signature to match, or <see langword="default"/> to match any signature. </param>
    /// <returns> The method, or <see langword="null"/> if it could not be resolved. </returns>
    public MethodDesc? GetMethod(TypeDesc? type, string name, in MethodSig sig = default)
    {
        if (type == null) {
            return null;
        }

        var key = new MethodKey(type, name, sig);
        if (_methods.TryGetValue(key, out var cached)) {
            return cached;
        }

        var method = type.FindMethod(name, sig, throwIfNotFound: false);
        _methods[key] = method;
        return method;
    }

    /// <summary>
    /// Drops the resolution caches, so that types and members of a newly loaded module become visible.
    /// </summary>
    /// <remarks> Called by <see cref="ModuleResolver"/> whenever a module is added to it. </remarks>
    internal void Invalidate()
    {
        _types.Clear();
        _methods.Clear();
    }

    // ---- Types -----------------------------------------------------------------------------

    public TypeDefOrSpec? Object => _object ??= GetType("System.Object");
    public TypeDefOrSpec? String => _string ??= GetType("System.String");
    public TypeDefOrSpec? Type => _type ??= GetType("System.Type");
    public TypeDefOrSpec? Delegate => _delegate ??= GetType("System.Delegate");
    public TypeDefOrSpec? Exception => _exception ??= GetType("System.Exception");
    public TypeDefOrSpec? ArgumentNullException => _argumentNullException ??= GetType("System.ArgumentNullException");
    public TypeDefOrSpec? IDisposable => _disposable ??= GetType("System.IDisposable");
    public TypeDefOrSpec? StringBuilder => _stringBuilder ??= GetType("System.Text.StringBuilder");
    public TypeDefOrSpec? ListOfT => _listOfT ??= GetType("System.Collections.Generic.List`1");
    public TypeDefOrSpec? IEnumerableOfT => _enumerableOfT ??= GetType("System.Collections.Generic.IEnumerable`1");
    public TypeDefOrSpec? Enumerable => _enumerable ??= GetType("System.Linq.Enumerable");
    public TypeDefOrSpec? MemoryMarshal => _memoryMarshal ??= GetType("System.Runtime.InteropServices.MemoryMarshal");
    public TypeDefOrSpec? RuntimeHelpers => _runtimeHelpers ??= GetType("System.Runtime.CompilerServices.RuntimeHelpers");
    public TypeDefOrSpec? BitOperations => _bitOperations ??= GetType("System.Numerics.BitOperations");
    public TypeDefOrSpec? CompilerGeneratedAttribute => _compilerGeneratedAttribute ??= GetType("System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    /// <summary> The attribute marking a C# 15 closed type. Not available before .NET 11. </summary>
    public TypeDefOrSpec? IsClosedTypeAttribute => _isClosedTypeAttribute ??= GetType(TypeUtils.IsClosedAttribFullName);

    /// <summary> The attribute marking a C# 15 union type. Not available before .NET 11. </summary>
    public TypeDefOrSpec? UnionAttribute => _unionAttribute ??= GetType(TypeUtils.UnionAttribFullName);

    /// <summary> The interface implemented by every C# 15 union type. Not available before .NET 11. </summary>
    public TypeDefOrSpec? IUnion => _iUnion ??= GetType(TypeUtils.UnionInterfaceFullName);

    TypeDefOrSpec? _object, _string, _type, _delegate, _exception, _argumentNullException, _disposable;
    TypeDefOrSpec? _stringBuilder, _listOfT, _enumerableOfT, _enumerable;
    TypeDefOrSpec? _memoryMarshal, _runtimeHelpers, _bitOperations, _compilerGeneratedAttribute;
    TypeDefOrSpec? _isClosedTypeAttribute, _unionAttribute, _iUnion;

    // ---- Members ---------------------------------------------------------------------------

    public MethodDesc? CompilerGeneratedAttributeCtor
        => _compilerGeneratedAttributeCtor ??= GetMethod(CompilerGeneratedAttribute, ".ctor");

    public MethodDesc? IsClosedTypeAttributeCtor
        => _isClosedTypeAttributeCtor ??= GetMethod(IsClosedTypeAttribute, ".ctor");

    public MethodDesc? UnionAttributeCtor
        => _unionAttributeCtor ??= GetMethod(UnionAttribute, ".ctor");

    public MethodDesc? IUnionGetValue
        => _iUnionGetValue ??= GetMethod(IUnion, "get_Value");

    public MethodDesc? StringBuilderCtor
        => _stringBuilderCtor ??= StringBuilder is { } sb
            ? GetMethod(sb, ".ctor", new MethodSig(PrimType.Void, [PrimType.String]))
            : null;

    public MethodDesc? StringBuilderAppendString
        => _stringBuilderAppendString ??= StringBuilder is { } sb
            ? GetMethod(sb, "Append", new MethodSig(sb, [PrimType.String]))
            : null;

    public MethodDesc? StringBuilderAppendChar
        => _stringBuilderAppendChar ??= StringBuilder is { } sb
            ? GetMethod(sb, "Append", new MethodSig(sb, [PrimType.Char]))
            : null;

    public MethodDesc? StringBuilderToString
        => _stringBuilderToString ??= StringBuilder is { } sb
            ? GetMethod(sb, "ToString", new MethodSig(PrimType.String, []))
            : null;

    /// <summary> The unbound <c>List&lt;T&gt;.Add(T)</c> method. </summary>
    public MethodDesc? ListAdd
        => _listAdd ??= ListOfT is { GenericParams: [var t] } list
            ? GetMethod(list, "Add", new MethodSig(PrimType.Void, [t]))
            : null;

    /// <summary> The unbound <c>List&lt;T&gt;.get_Count()</c> method. </summary>
    public MethodDesc? ListCount
        => _listCount ??= ListOfT is { GenericParams: [var t] } list
            ? GetMethod(list, "get_Count", new MethodSig(PrimType.Int32, []))
            : null;

    /// <summary> The unbound <c>List&lt;T&gt;.get_Item(int)</c> method. </summary>
    public MethodDesc? ListItem
        => _listItem ??= ListOfT is { GenericParams: [var t] } list
            ? GetMethod(list, "get_Item", new MethodSig(t, [PrimType.Int32]))
            : null;

    /// <summary> The unbound <c>List&lt;T&gt;.ToArray()</c> method. </summary>
    public MethodDesc? ListToArray
        => _listToArray ??= ListOfT is { GenericParams: [var t] } list
            ? GetMethod(list, "ToArray", new MethodSig(t.CreateArray(), []))
            : null;

    /// <summary> The unbound <c>List&lt;T&gt;.ctor(int)</c> method. </summary>
    public MethodDesc? ListCtorCapacity
        => _listCtorCapacity ??= GetMethod(ListOfT, ".ctor", new MethodSig(PrimType.Void, [PrimType.Int32]));

    /// <summary> The unbound <c>Array.Empty&lt;T&gt;()</c> method. </summary>
    public MethodDesc? ArrayEmpty => _arrayEmpty ??= GetMethod(GetType("System.Array"), "Empty");

    /// <summary> The unbound <c>MemoryMarshal.CreateSpan&lt;T&gt;(ref T, int)</c> method. </summary>
    public MethodDesc? MemoryMarshalCreateSpan
        => _memoryMarshalCreateSpan ??= GetMethod(MemoryMarshal, "CreateSpan");

    /// <summary> The unbound <c>MemoryMarshal.GetArrayDataReference&lt;T&gt;(T[])</c> method. </summary>
    public MethodDesc? MemoryMarshalGetArrayDataReference
        => _memoryMarshalGetArrayDataReference ??= GetMethod(MemoryMarshal, "GetArrayDataReference");

    /// <summary> <c>BitOperations.PopCount(uint)</c>. </summary>
    public MethodDesc? BitOperationsPopCount
        => _bitOperationsPopCount ??= GetMethod(BitOperations, "PopCount", new MethodSig(PrimType.Int32, [PrimType.UInt32]));

    /// <summary> <c>string.Substring(int, int)</c>. </summary>
    public MethodDesc? StringSubstring
        => _stringSubstring ??= GetMethod(String, "Substring", new MethodSig(PrimType.String, [PrimType.Int32, PrimType.Int32]));

    MethodDesc? _compilerGeneratedAttributeCtor, _isClosedTypeAttributeCtor, _unionAttributeCtor, _iUnionGetValue;
    MethodDesc? _stringBuilderCtor, _stringBuilderAppendString, _stringBuilderAppendChar, _stringBuilderToString;
    MethodDesc? _listAdd, _listCount, _listItem, _listToArray, _listCtorCapacity;
    MethodDesc? _arrayEmpty, _memoryMarshalCreateSpan, _memoryMarshalGetArrayDataReference;
    MethodDesc? _bitOperationsPopCount, _stringSubstring;

    readonly record struct MethodKey(TypeDesc Type, string Name, MethodSig Sig);
}
