namespace Cilica.Tests.Passes;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Util;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class StringConcatLoopTests : PassTestBase
{
    private readonly MethodDesc _concat;
    private readonly MethodDesc _charToString;

    public StringConcatLoopTests(ModuleResolverFixture fixture) : base(fixture)
    {
        _concat = _modResolver.SysTypes.String.FindMethod(
            "Concat", new MethodSig(PrimType.String, [PrimType.String, PrimType.String]))!;
        _charToString = _modResolver.SysTypes.Char.FindMethod(
            "ToString", new MethodSig(PrimType.String, []))!;
    }

    private MethodDesc? SbAppend(TypeDesc param)
    {
        var sbType = _modResolver.FindType("System.Text.StringBuilder");
        return sbType?.FindMethod("Append", new MethodSig(sbType, [param]), throwIfNotFound: false);
    }

    private static int CountCalls(MethodBody body, MethodDesc method)
        => body.Instructions().OfType<CallInst>().Count(c => c.Method == method);

    [Fact]
    public void AppendingInsideALoopUsesAStringBuilder()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.String, PrimType.String);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);

        var index = loop.CreateInductor();
        var s = loop.CreateAccum(body.Args[0], curr => loop.Latch.CreateCall(_concat, curr, body.Args[1]));
        loop.Build(h => h.CreateSlt(index, ConstInt.CreateI(3)), _ => { });
        loop.Exit.CreateReturn(s);

        Run(body, new StringConcatLoop());

        Assert.Equal(0, CountCalls(body, _concat));
        Assert.Single(body.Instructions().OfType<NewObjInst>());
        Assert.NotNull(SbAppend(PrimType.String));

        var run = Utils.Compile<Func<string, string, string>>(body);
        Assert.Equal("xababab", run("x", "ab"));
    }

    [Fact]
    public void MultipleAppendsKeepTheirSourceOrder()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.String, PrimType.String, PrimType.String);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);

        var index = loop.CreateInductor();
        var s = loop.CreateAccum(body.Args[0], curr => {
            var first = loop.Latch.CreateCall(_concat, curr, body.Args[1]);
            return loop.Latch.CreateCall(_concat, first, body.Args[2]);
        });
        loop.Build(h => h.CreateSlt(index, ConstInt.CreateI(2)), _ => { });
        loop.Exit.CreateReturn(s);

        Run(body, new StringConcatLoop());

        Assert.Equal(0, CountCalls(body, _concat));

        var run = Utils.Compile<Func<string, string, string, string>>(body);
        Assert.Equal("xabab", run("x", "a", "b"));
    }

    [Fact]
    public void AppendedCharUsesTheCharOverloadAndDropsCharToString()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.String, PrimType.Char);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);

        var index = loop.CreateInductor();
        var s = loop.CreateAccum(body.Args[0], curr => {
            var str = loop.Latch.CreateCall(_charToString, body.Args[1]);
            return loop.Latch.CreateCall(_concat, curr, str);
        });
        loop.Build(h => h.CreateSlt(index, ConstInt.CreateI(2)), _ => { });
        loop.Exit.CreateReturn(s);

        Run(body, new StringConcatLoop());

        Assert.Equal(0, CountCalls(body, _concat));
        Assert.Equal(0, CountCalls(body, _charToString));

        var appendChar = SbAppend(PrimType.Char);
        Assert.NotNull(appendChar);
        Assert.Equal(1, CountCalls(body, appendChar!));

        var run = Utils.Compile<Func<string, char, string>>(body);
        Assert.Equal("xcc", run("x", 'c'));
    }

    [Fact]
    public void ALoopThatIsNeverEnteredReturnsTheInitialValue()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.String, PrimType.String);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);

        var index = loop.CreateInductor();
        var s = loop.CreateAccum(body.Args[0], curr => loop.Latch.CreateCall(_concat, curr, body.Args[1]));
        loop.Build(h => h.CreateSlt(index, ConstInt.CreateI(0)), _ => { });
        loop.Exit.CreateReturn(s);

        Run(body, new StringConcatLoop());

        Assert.Equal(0, CountCalls(body, _concat));
        var run = Utils.Compile<Func<string, string, string>>(body);
        Assert.Equal("x", run("x", "ab"));
    }

    [Fact]
    public void AnAdditionalUseInsideTheLoopPreventsTheRewrite()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.String, PrimType.String);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);

        var index = loop.CreateInductor();
        var s = loop.CreateAccum(body.Args[0], curr => loop.Latch.CreateCall(_concat, curr, body.Args[1]));
        loop.Build(h => h.CreateSlt(index, ConstInt.CreateI(3)),
                   b => b.CreateCall(_concat, s, s));
        loop.Exit.CreateReturn(s);

        Run(body, new StringConcatLoop());

        // One concat in the latch, one extra use inside the body: no rewrite.
        Assert.Equal(2, CountCalls(body, _concat));
        Assert.Empty(body.Instructions().OfType<NewObjInst>());
    }

    [Fact]
    public void ALoopInsideAProtectedRegionIsNotRewritten()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.String, PrimType.String);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var realExit = body.CreateBlock();
        entry.SetBranch(guarded);
        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, _modResolver.Import(typeof(Exception))));

        var loop = new LoopBuilder(guarded);
        guarded.SetBranch(loop.EntryBlock);

        var index = loop.CreateInductor();
        var s = loop.CreateAccum(body.Args[0], curr => loop.Latch.CreateCall(_concat, curr, body.Args[1]));
        loop.Build(h => h.CreateSlt(index, ConstInt.CreateI(3)), _ => { });

        var exitInRegion = loop.Exit.Block;
        exitInRegion.SetBranch(new LeaveInst(realExit));
        handler.SetBranch(new LeaveInst(realExit));

        var phi = realExit.InsertPhi(new PhiInst(PrimType.String, (exitInRegion, body.Args[0]), (handler, body.Args[0])));
        realExit.SetBranch(new ReturnInst(phi));

        // The accumulator has no use outside the loop, so the protected-region check
        // is the only reason to reject the rewrite.
        Run(body, new StringConcatLoop());

        Assert.Equal(1, CountCalls(body, _concat));
        Assert.Empty(body.Instructions().OfType<NewObjInst>());
    }

    [Fact]
    public void ConcatenationOnTheRightHandSidePreventsTheRewrite()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.String, PrimType.String);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);

        var index = loop.CreateInductor();
        // s = a + s  =>  the accumulator is the right operand, not the concatenation base.
        var s = loop.CreateAccum(body.Args[0], curr => loop.Latch.CreateCall(_concat, body.Args[1], curr));
        loop.Build(h => h.CreateSlt(index, ConstInt.CreateI(3)), _ => { });
        loop.Exit.CreateReturn(s);

        Run(body, new StringConcatLoop());

        Assert.Equal(1, CountCalls(body, _concat));
        Assert.Empty(body.Instructions().OfType<NewObjInst>());
    }

    [Fact]
    public void TheBuilderIsAllocatedInThePreHeader()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.String, PrimType.String);
        var entry = body.CreateBlock();
        var loop = new LoopBuilder(entry);
        entry.SetBranch(loop.EntryBlock);

        var index = loop.CreateInductor();
        var s = loop.CreateAccum(body.Args[0], curr => loop.Latch.CreateCall(_concat, curr, body.Args[1]));
        loop.Build(h => h.CreateSlt(index, ConstInt.CreateI(3)), _ => { });
        loop.Exit.CreateReturn(s);

        Run(body, new StringConcatLoop());

        var alloc = Assert.Single(body.Instructions().OfType<NewObjInst>());
        Assert.Same(loop.PreHeader.Block, alloc.Block);
        Assert.Same(loop.PreHeader.Block.Last.Prev, alloc);
    }
}
