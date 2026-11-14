namespace Cilica.Passes;

using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;

/// <summary>
/// Replaces string concatenation performed inside a loop (<c>s += x</c>) with a single
/// <see cref="System.Text.StringBuilder"/> allocated in the loop pre-header.
/// </summary>
/// <remarks>
/// A loop that keeps appending to a string rebuilds the whole string on every iteration
/// (<c>O(n²)</c> copying) and allocates a temporary for each step:
/// <code>
/// pre:  br header                          pre:  sb = newobj StringBuilder::.ctor(init)
/// hdr:  p = phi String [pre: init]          hdr:  br cond ? body : exit
///            [latch: c2]
///       br cond ? body : exit
/// body: ...                                body: ...
/// ltch: c1 = call String::Concat(p, a)      ltch: callvirt sb.Append(a)
///       c2 = call String::Concat(c1, b)          callvirt sb.Append(b)
///       br header                                br header
/// exit: use(p)                             exit: t = callvirt sb.ToString()
///                                                use(t)
/// </code>
/// The rewrite preserves the exact number and order of <c>Append</c> calls, so the
/// resulting string is identical. Exceptions thrown by an appended argument's evaluation
/// are also preserved because each <c>Append</c> stays at the position of the <c>Concat</c>
/// it replaces.
/// </remarks>
public class StringConcatLoop : IMethodPass
{
    public MethodPassResult Run(MethodTransformContext ctx)
    {
        var resolver = ctx.Compilation.Resolver;
        if (resolver.FindType("System.Text.StringBuilder") is not TypeDef sbType) {
            return MethodInvalidations.None;
        }

        var ctor = sbType.FindMethod(".ctor", new MethodSig(PrimType.Void, [PrimType.String]), throwIfNotFound: false);
        var appendStr = sbType.FindMethod("Append", new MethodSig(sbType, [PrimType.String]), throwIfNotFound: false);
        var appendChar = sbType.FindMethod("Append", new MethodSig(sbType, [PrimType.Char]), throwIfNotFound: false);
        var toString = sbType.FindMethod("ToString", new MethodSig(PrimType.String, []), throwIfNotFound: false);
        if (ctor == null || appendStr == null || toString == null) {
            return MethodInvalidations.None;
        }

        var loopAnalysis = ctx.GetAnalysis<LoopAnalysis>();
        var domTree = ctx.GetAnalysis<DominatorTree>();
        var regions = ctx.GetAnalysis<ProtectedRegionAnalysis>();

        // Materialize, because the rewrite mutates the method while we iterate.
        var loops = loopAnalysis.GetInnermostLoops().ToList();

        int numChanges = 0;
        foreach (var loop in loops) {
            if (TryRewrite(ctx, loop, ctor, appendStr, appendChar, toString, domTree, regions)) {
                numChanges++;
            }
        }

        if (numChanges == 0) {
            return MethodInvalidations.None;
        }
        ctx.Logger.Trace($"StringConcatLoop: Rewrote {numChanges} loop(s) to use StringBuilder");
        return MethodInvalidations.DataFlow;
    }

    private static bool TryRewrite(
        MethodTransformContext ctx, LoopInfo loop,
        MethodDesc ctor, MethodDesc appendStr, MethodDesc? appendChar, MethodDesc toString,
        DominatorTree domTree, ProtectedRegionAnalysis regions)
    {
        var preHeader = loop.GetPreheader();
        var latch = loop.GetLatch();
        var exit = loop.GetExit();
        if (preHeader == null || latch == null || exit == null) {
            return false;
        }
        // If the loop body is protected, an exception may escape while only part of the
        // string has been appended, so the handler could observe a different value.
        if (regions.GetBlockRegion(loop.Header) != regions.Root) {
            return false;
        }

        foreach (var phi in GetPhis(loop.Header)) {
            if (phi.ResultType != PrimType.String || phi.NumArgs != 2) {
                continue;
            }
            if (TryRewritePhi(ctx, loop, phi, preHeader, latch, exit, ctor, appendStr, appendChar, toString, domTree)) {
                return true;
            }
        }
        return false;
    }

    private static bool TryRewritePhi(
        MethodTransformContext ctx, LoopInfo loop, PhiInst phi,
        BasicBlock preHeader, BasicBlock latch, BasicBlock exit,
        MethodDesc ctor, MethodDesc appendStr, MethodDesc? appendChar, MethodDesc toString,
        DominatorTree domTree)
    {
        var initValue = phi.GetValue(preHeader);
        if (initValue == phi) {
            return false;
        }

        // Walk backwards through the concatenation chain, e.g. c2 = Concat(c1, b); c1 = Concat(p, a).
        var chain = new List<CallInst>();
        var appended = new List<Value>();
        var seen = new HashSet<Instruction>();

        var current = phi.GetValue(latch);
        while (current != phi) {
            if (current is not CallInst { NumArgs: 2 } concat) return false;
            if (!IsStringConcat(concat)) return false;
            if (!loop.Contains(concat.Block)) return false;
            if (!seen.Add(concat)) return false;

            var next = concat.Args[0];
            var value = concat.Args[1];
            // `s = s + s` and other self-referencing chains cannot be rewritten.
            if (value == phi || value == concat) return false;
            if (value is Instruction valueInst && seen.Contains(valueInst)) return false;

            chain.Add(concat);
            appended.Add(value);
            current = next;
        }
        if (chain.Count == 0) {
            return false;
        }
        // Restore source order: the chain was collected from the last Concat to the first.
        chain.Reverse();
        appended.Reverse();

        // Every Concat in the chain must be consumed by exactly the next one (or the phi).
        foreach (var concat in chain) {
            if (concat.Uses().Count() != 1) {
                return false;
            }
        }

        // `p` may only be used as the first chain link's left operand, and outside the loop.
        var chainStart = chain[0];
        var outsideUses = new List<UseRef>();
        foreach (var use in phi.Uses()) {
            if (use.Parent == chainStart) {
                continue;
            }
            if (use.Parent is PhiInst) {
                return false;
            }
            if (loop.Contains(use.Parent.Block)) {
                return false;
            }
            if (!domTree.IsReachable(use.Parent.Block) || !domTree.Dominates(exit, use.Parent.Block)) {
                return false;
            }
            outsideUses.Add(use);
        }

        // Detect `Append(char)` opportunities: `Concat(p, ch.ToString())`.
        var charAppends = new Dictionary<CallInst, (CallInst CharToString, Value Char)>();
        if (appendChar != null) {
            for (int i = 0; i < chain.Count; i++) {
                if (appended[i] is not CallInst { NumArgs: 1 } charToString) continue;
                if (charToString.Method.Name != "ToString") continue;
                if (!charToString.Method.DeclaringType.IsCorelibType(typeof(char))) continue;
                if (charToString.Method.ReturnType != PrimType.String) continue;
                if (charToString.Uses().Count() != 1) continue;
                charAppends.Add(chain[i], (charToString, charToString.Args[0]));
            }
        }

        // Allocate the builder once, before entering the loop.
        var builder = new IRBuilder(preHeader.Last, InsertionDir.Before);
        var sb = builder.CreateNewObj(ctor, initValue);

        for (int i = 0; i < chain.Count; i++) {
            var concat = chain[i];
            builder.SetPosition(concat, InsertionDir.Before);

            if (charAppends.TryGetValue(concat, out var charAppend)) {
                builder.CreateCallVirt(appendChar!, sb, charAppend.Char);
                charAppend.CharToString.Remove();
            } else {
                builder.CreateCallVirt(appendStr, sb, appended[i]);
            }
            concat.Remove();
        }

        if (outsideUses.Count > 0) {
            // Materialize the final string once, at the loop exit. `Before` on a block skips phis/guards.
            var exitBuilder = new IRBuilder(exit, InsertionDir.Before);
            var result = exitBuilder.CreateCallVirt(toString, sb);
            foreach (var use in outsideUses) {
                use.Operand = result;
            }
        }

        phi.Remove();
        ctx.Logger.Trace($"StringConcatLoop: Replaced {chain.Count} concatenation(s) in loop '{loop.Header}'");
        return true;
    }

    private static IEnumerable<PhiInst> GetPhis(BasicBlock block)
    {
        for (var inst = block.First; inst is PhiInst or GuardInst; inst = inst.Next) {
            if (inst is PhiInst phi) {
                yield return phi;
            }
        }
    }

    private static bool IsStringConcat(CallInst call)
    {
        return call.Method.Name == "Concat" &&
               call.Method.DeclaringType.IsCorelibType(typeof(string)) &&
               call.Args[0].ResultType == PrimType.String &&
               call.Args[1].ResultType == PrimType.String;
    }
}
