using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace WoodenNut.WNAE
{
    #region Expander

    /// <summary>
    /// State に付いた <see cref="WNAE_ParameterCalculation"/> を、
    /// Parameter Driver と遷移条件だけで構成された Sub State Machine へ展開する。
    ///
    /// Parameter Driver の Add は定数加算しかできないため、演算の基本形は
    /// 「入力を上位ビットから崩しながら acc に定数を足し込む」チェーンになる。
    /// </summary>
    internal static class WNAECalcExpander
    {
        private const string ParameterPrefix = "WNAE/Calc/";
        private const float ColumnWidth = 240f;
        private const float RowHeight = 55f;

        private struct Target
        {
            public VirtualStateMachine Parent;
            public VirtualState State;
            public Vector3 Position;
            public List<WNAE_ParameterCalculation> Calculations;
        }

        public static void Expand(VirtualControllerContext controllerContext)
        {
            var index = 0;

            foreach (var controller in controllerContext.Controllers.Values.ToList())
            {
                if (controller == null) continue;

                VirtualClip clip = null;

                foreach (var layer in controller.Layers.ToList())
                {
                    var root = layer.StateMachine;
                    if (root == null) continue;

                    // 遷移の張り替えでレイヤー全体を歩くため、先に対象を集めてから処理する
                    var targets = new List<Target>();
                    Collect(controller, root, targets);

                    foreach (var target in targets)
                    {
                        clip = clip ?? VirtualClip.Create("WNAE Calc Empty");
                        ExpandState(controllerContext, controller, root, target, clip, ref index);
                    }
                }
            }
        }

        private static void Collect(
            VirtualAnimatorController controller, VirtualStateMachine stateMachine, List<Target> targets)
        {
            if (stateMachine == null) return;

            foreach (var child in stateMachine.StateMachines.ToList())
            {
                Collect(controller, child.StateMachine, targets);
            }

            foreach (var child in stateMachine.States.ToList())
            {
                var state = child.State;
                if (state == null) continue;

                var calculations = state.Behaviours.OfType<WNAE_ParameterCalculation>().ToList();
                if (calculations.Count == 0) continue;

                var label = $"{controller.Name} / {state.Name}";

                // SMB はユーザーのコントローラアセットの実体なので、リストから外すだけにする
                state.Behaviours = state.Behaviours.RemoveAll(b => b is WNAE_ParameterCalculation);

                if (state.Motion != null || !state.Behaviours.IsEmpty)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[WNAE] Parameter Calculation ({label}): この State は演算用の Sub State Machine に" +
                        "置き換えられるため、Motion と他の Behaviour は失われます。");
                }

                var accepted = new List<WNAE_ParameterCalculation>();
                foreach (var calculation in calculations)
                {
                    var issues = Validate(calculation);

                    foreach (var issue in issues)
                    {
                        var message = $"[WNAE] Parameter Calculation ({label}): {issue.Message}";
                        if (issue.Level == WNAEIssueLevel.Error) UnityEngine.Debug.LogError(message);
                        else if (issue.Level == WNAEIssueLevel.Warning) UnityEngine.Debug.LogWarning(message);
                        else UnityEngine.Debug.Log(message);
                    }

                    if (issues.Any(i => i.Level == WNAEIssueLevel.Error)) continue;
                    accepted.Add(calculation);
                }

                if (accepted.Count == 0) continue;

                targets.Add(new Target
                {
                    Parent = stateMachine,
                    State = state,
                    Position = child.Position,
                    Calculations = accepted,
                });
            }
        }

        /// <summary>元の State を、演算チェーンを収めた Sub State Machine で置き換える。</summary>
        private static void ExpandState(
            VirtualControllerContext controllerContext,
            VirtualAnimatorController controller,
            VirtualStateMachine layerRoot,
            Target target,
            VirtualClip clip,
            ref int index)
        {
            var parent = target.Parent;
            var state = target.State;

            var sub = VirtualStateMachine.Create(controllerContext.CloneContext, "WNAE Calc/" + state.Name);

            parent.StateMachines = parent.StateMachines.Add(new VirtualStateMachine.VirtualChildStateMachine
            {
                StateMachine = sub,
                Position = target.Position,
            });

            var terminals = new List<VirtualState>();
            VirtualState head = null;

            foreach (var calculation in target.Calculations)
            {
                var ctx = new CalcContext
                {
                    StateMachine = sub,
                    Clip = clip,
                    WriteDefaults = state.WriteDefaultValues,
                    Prefix = ParameterPrefix + index++,
                    Calculation = calculation,
                };

                DeclareParameters(controller, ctx);

                var entry = Build(ctx);

                if (head == null)
                {
                    head = entry.First;
                    sub.DefaultState = head;
                }
                else
                {
                    // 直前の演算の終端から、この演算の先頭へ繋ぐ
                    Link(terminals, entry.First, null);
                }

                terminals = entry.Terminals;
            }

            if (head == null) return;

            // 出ていく遷移は終端へ移す。自己遷移が含まれていても、この後の張り替えで先頭に向く
            var outgoing = state.Transitions;
            state.Transitions = ImmutableList<VirtualStateTransition>.Empty;

            foreach (var terminal in terminals)
            {
                terminal.Transitions = outgoing;
            }

            // 元の State を指していた遷移をすべて先頭 State へ向ける。
            // Sub State Machine 自体ではなく中の State を直接指すことで、
            // AnyState から Sub State Machine を指せるかという不確実性を避けている。
            Repoint(layerRoot, state, head);

            if (parent.DefaultState == state)
            {
                // Default State は Sub State Machine 内の State を指せないため、Entry から入れる
                var entryTransition = VirtualTransition.Create();
                entryTransition.SetDestination(sub);
                parent.EntryTransitions = parent.EntryTransitions.Insert(0, entryTransition);

                parent.DefaultState = parent.States
                    .Select(cs => cs.State)
                    .FirstOrDefault(s => s != null && s != state);
            }

            parent.States = parent.States.RemoveAll(cs => cs.State == state);
        }

        private static void Repoint(VirtualStateMachine stateMachine, VirtualState from, VirtualState to)
        {
            if (stateMachine == null) return;

            foreach (var transition in stateMachine.AnyStateTransitions) Retarget(transition, from, to);
            foreach (var transition in stateMachine.EntryTransitions) Retarget(transition, from, to);

            foreach (var pair in stateMachine.StateMachineTransitions)
            {
                foreach (var transition in pair.Value) Retarget(transition, from, to);
            }

            foreach (var child in stateMachine.States)
            {
                if (child.State == null) continue;
                foreach (var transition in child.State.Transitions) Retarget(transition, from, to);
            }

            foreach (var child in stateMachine.StateMachines) Repoint(child.StateMachine, from, to);
        }

        private static void Retarget(VirtualTransitionBase transition, VirtualState from, VirtualState to)
        {
            if (transition.DestinationState == from) transition.SetDestination(to);
        }

        private static void DeclareParameters(VirtualAnimatorController controller, CalcContext ctx)
        {
            var calculation = ctx.Calculation;
            var usesB = WNAECalc.UsesB(calculation.operation);

            DeclareOperand(controller, calculation.parameterA);
            if (usesB) DeclareOperand(controller, calculation.parameterB);
            DeclareOperand(controller, calculation.parameterC);

            // 中間パラメータは同期不要なので VRCExpressionParameters には追加しない
            WNAEAnimator.EnsureIntParameter(controller, ctx.Ta);
            if (usesB) WNAEAnimator.EnsureIntParameter(controller, ctx.Tb);
            WNAEAnimator.EnsureIntParameter(controller, ctx.Acc);
        }

        private static void DeclareOperand(VirtualAnimatorController controller, string name)
        {
            if (controller.Parameters.TryGetValue(name, out var existing))
            {
                if (existing.type != AnimatorControllerParameterType.Int)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[WNAE] Parameter Calculation: \"{name}\" は {existing.type} 型です。" +
                        "Int として扱われるため、値が意図せず変換される可能性があります。");
                }
                return;
            }

            WNAEAnimator.EnsureIntParameter(controller, name);
        }

        #region Validation

        public static List<WNAEIssue> Validate(WNAE_ParameterCalculation calculation)
        {
            var issues = new List<WNAEIssue>();

            if (string.IsNullOrWhiteSpace(calculation.parameterA))
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "Parameter A が指定されていません。"));
            }

            if (WNAECalc.UsesB(calculation.operation) && string.IsNullOrWhiteSpace(calculation.parameterB))
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "Parameter B が指定されていません。"));
            }

            if (string.IsNullOrWhiteSpace(calculation.parameterC))
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "Parameter C が指定されていません。"));
            }

            if (calculation.bitWidth < WNAECalc.MinBitWidth || calculation.bitWidth > WNAECalc.MaxBitWidth)
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    $"Bit Width は {WNAECalc.MinBitWidth}〜{WNAECalc.MaxBitWidth} で指定してください" +
                    $"（現在 {calculation.bitWidth}）。"));
            }

            var states = EstimateStateCount(calculation);
            if (states > 512)
            {
                issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                    $"この設定では約 {states} State が生成されます。Bit Width を下げると大幅に減らせます。"));
            }

            return issues;
        }

        /// <summary>
        /// 生成される State 数。Init と Store を含む、実際の生成数と一致する値を返す。
        /// </summary>
        public static int EstimateStateCount(WNAE_ParameterCalculation calculation)
        {
            var n = Mathf.Clamp(calculation.bitWidth, WNAECalc.MinBitWidth, WNAECalc.MaxBitWidth);
            var values = 1 << n;

            switch (calculation.operation)
            {
                // Init + Store
                case WNAECalcOperation.Copy:
                    return 2;

                // Init + acc=a + 2n + 桁あふれ補正 2 + Store
                case WNAECalcOperation.Add:
                case WNAECalcOperation.Sub:
                    return 2 * n + 5;

                // Init + 2n + Store
                case WNAECalcOperation.Not:
                    return 2 * n + 2;

                // Init + 4n + Store
                case WNAECalcOperation.And:
                case WNAECalcOperation.Or:
                case WNAECalcOperation.Xor:
                case WNAECalcOperation.Xnor:
                    return 4 * n + 2;

                // Init + 分岐 2^n + 受け皿 + 上位ビット落とし 2n + Store
                case WNAECalcOperation.Mul:
                    return values + 2 * n + 3;

                // Init + 分岐 n + 受け皿 + 上位ビット落とし 2(n-1) + Store
                case WNAECalcOperation.ShiftLeft:
                    return n <= 1 ? n + 3 : 3 * n + 1;

                // Init + Σ(分岐 + 2(n-s)) + 受け皿 + Store
                case WNAECalcOperation.ShiftRight:
                    return n * n + 2 * n + 3;

                // Init + Σ(分岐 + 2(top+1)) + ゼロ除算 + Store
                case WNAECalcOperation.Div:
                case WNAECalcOperation.Mod:
                {
                    var levels = 0;
                    for (var b = 1; b < values; b++)
                    {
                        var top = 0;
                        while ((long)b << (top + 1) <= values - 1) top++;
                        levels += top + 1;
                    }
                    return values + 2 * levels + 2;
                }

                default:
                    return 0;
            }
        }

        #endregion

        #region Chain construction

        private class CalcContext
        {
            public VirtualStateMachine StateMachine;
            public VirtualClip Clip;
            public bool WriteDefaults;
            public string Prefix;
            public WNAE_ParameterCalculation Calculation;

            public int Column;

            public string Ta => Prefix + "/ta";
            public string Tb => Prefix + "/tb";
            public string Acc => Prefix + "/acc";

            public int BitWidth => Mathf.Clamp(
                Calculation.bitWidth, WNAECalc.MinBitWidth, WNAECalc.MaxBitWidth);

            public int Modulus => 1 << BitWidth;

            public VirtualState NewState(string label, int row, Action<VRCAvatarParameterDriver> fill)
            {
                var state = StateMachine.AddState(
                    label, Clip, new Vector3(Column * ColumnWidth, row * RowHeight, 0f));
                state.WriteDefaultValues = WriteDefaults;

                if (fill == null) return state;

                var driver = WNAEAnimator.CreateDriver("WNAE Calc " + label, localOnly: false);
                fill(driver);
                if (driver.parameters.Count > 0) state.Behaviours = state.Behaviours.Add(driver);

                return state;
            }
        }

        private struct BuildResult
        {
            public VirtualState First;
            public List<VirtualState> Terminals;
        }

        private static void Link(
            IEnumerable<VirtualState> from, VirtualState to, IEnumerable<AnimatorCondition> conditions)
        {
            foreach (var state in from)
            {
                state.Transitions = state.Transitions.Add(WNAEAnimator.CreateTransition(to, conditions));
            }
        }

        /// <summary>
        /// parameter が [min, max] の外だったときの受け皿へ繋ぐ。
        /// Animator の条件は AND のみなので、下限割れと上限超えを 2 本の遷移に分けて表現する。
        /// </summary>
        private static void LinkOutOfRange(
            IEnumerable<VirtualState> from, VirtualState to, string parameter, int min, int max)
        {
            var states = from as IList<VirtualState> ?? from.ToList();

            Link(states, to, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, parameter, min) });
            Link(states, to, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, parameter, max) });
        }

        private static AnimatorCondition BitSet(string parameter, int bit)
        {
            return WNAEAnimator.Condition(AnimatorConditionMode.Greater, parameter, bit - 1);
        }

        private static AnimatorCondition BitClear(string parameter, int bit)
        {
            return WNAEAnimator.Condition(AnimatorConditionMode.Less, parameter, bit);
        }

        /// <summary>
        /// src の bit k を判定する 2 分岐を 1 段追加する。
        /// 両側に明示的な条件を付けるため、遷移の並び順に依存せず、どちらも即座に成立する。
        /// </summary>
        private static List<VirtualState> BitLevel(
            CalcContext ctx,
            List<VirtualState> previous,
            string source,
            int k,
            int row,
            Action<VRCAvatarParameterDriver> onSet,
            Action<VRCAvatarParameterDriver> onClear)
        {
            ctx.Column++;

            var bit = 1 << k;
            var set = ctx.NewState($"b{k}=1", row, onSet);
            var clear = ctx.NewState($"b{k}=0", row + 1, onClear);

            Link(previous, set, new[] { BitSet(source, bit) });
            Link(previous, clear, new[] { BitClear(source, bit) });

            return new List<VirtualState> { set, clear };
        }

        /// <summary>acc の上位ビットを落として mod 2^BitWidth にそろえる。</summary>
        private static List<VirtualState> StripHighBits(
            CalcContext ctx, List<VirtualState> previous, int fromBit, int toBit)
        {
            var current = previous;

            for (var k = fromBit; k >= toBit; k--)
            {
                var bit = 1 << k;
                current = BitLevel(ctx, current, ctx.Acc, k, 0,
                    d => WNAEAnimator.AddAdd(d, ctx.Acc, -bit),
                    null);
            }

            return current;
        }

        private static BuildResult Build(CalcContext ctx)
        {
            var calculation = ctx.Calculation;

            var init = ctx.NewState("Init", 0, d =>
            {
                WNAEAnimator.AddCopy(d, ctx.Ta, calculation.parameterA);
                if (WNAECalc.UsesB(calculation.operation))
                {
                    WNAEAnimator.AddCopy(d, ctx.Tb, calculation.parameterB);
                }
                WNAEAnimator.AddSet(d, ctx.Acc, 0f);
            });

            var current = new List<VirtualState> { init };

            switch (calculation.operation)
            {
                // Copy は Init が ta へ写した値をそのまま Store が書き出すため、チェーンを持たない
                case WNAECalcOperation.Copy:
                    break;

                case WNAECalcOperation.Add:
                    current = BuildAddSub(ctx, current, subtract: false);
                    break;
                case WNAECalcOperation.Sub:
                    current = BuildAddSub(ctx, current, subtract: true);
                    break;
                case WNAECalcOperation.Not:
                    current = BuildNot(ctx, current);
                    break;
                case WNAECalcOperation.And:
                case WNAECalcOperation.Or:
                case WNAECalcOperation.Xor:
                case WNAECalcOperation.Xnor:
                    current = BuildBitwise(ctx, current, calculation.operation);
                    break;
                case WNAECalcOperation.Mul:
                    current = BuildMul(ctx, current);
                    break;
                case WNAECalcOperation.ShiftLeft:
                    current = BuildShiftLeft(ctx, current);
                    break;
                case WNAECalcOperation.ShiftRight:
                    current = BuildShiftRight(ctx, current);
                    break;
                case WNAECalcOperation.Div:
                case WNAECalcOperation.Mod:
                    current = BuildDivMod(ctx, current, calculation.operation == WNAECalcOperation.Mod);
                    break;
            }

            // Mod は商ではなく剰余、Copy はチェーンを持たないので、どちらも ta が答え
            var source =
                calculation.operation == WNAECalcOperation.Mod ||
                calculation.operation == WNAECalcOperation.Copy
                    ? ctx.Ta
                    : ctx.Acc;

            ctx.Column++;
            var final = ctx.NewState("Store", 0,
                d => WNAEAnimator.AddCopy(d, calculation.parameterC, source));
            Link(current, final, null);

            return new BuildResult { First = init, Terminals = new List<VirtualState> { final } };
        }

        /// <summary>c = a ± b。tb を上位ビットから崩しながら acc に定数を足し引きする。</summary>
        private static List<VirtualState> BuildAddSub(
            CalcContext ctx, List<VirtualState> current, bool subtract)
        {
            var modulus = ctx.Modulus;

            ctx.Column++;
            var seed = ctx.NewState("acc=a", 0, d => WNAEAnimator.AddCopy(d, ctx.Acc, ctx.Ta));
            Link(current, seed, null);
            current = new List<VirtualState> { seed };

            for (var k = ctx.BitWidth - 1; k >= 0; k--)
            {
                var bit = 1 << k;
                current = BitLevel(ctx, current, ctx.Tb, k, 0,
                    d =>
                    {
                        WNAEAnimator.AddAdd(d, ctx.Tb, -bit);
                        WNAEAnimator.AddAdd(d, ctx.Acc, subtract ? -bit : bit);
                    },
                    null);
            }

            // 桁あふれ / 桁借りの補正
            ctx.Column++;
            if (subtract)
            {
                var wrap = ctx.NewState("wrap+", 0, d => WNAEAnimator.AddAdd(d, ctx.Acc, modulus));
                var keep = ctx.NewState("keep", 1, null);
                Link(current, wrap, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Acc, 0) });
                Link(current, keep, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Acc, -1) });
                return new List<VirtualState> { wrap, keep };
            }
            else
            {
                var wrap = ctx.NewState("wrap-", 0, d => WNAEAnimator.AddAdd(d, ctx.Acc, -modulus));
                var keep = ctx.NewState("keep", 1, null);
                Link(current, wrap,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Acc, modulus - 1) });
                Link(current, keep,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Acc, modulus) });
                return new List<VirtualState> { wrap, keep };
            }
        }

        /// <summary>c = ~a。ta を崩し、立っていなかったビットを acc に足す。</summary>
        private static List<VirtualState> BuildNot(CalcContext ctx, List<VirtualState> current)
        {
            for (var k = ctx.BitWidth - 1; k >= 0; k--)
            {
                var bit = 1 << k;
                current = BitLevel(ctx, current, ctx.Ta, k, 0,
                    d => WNAEAnimator.AddAdd(d, ctx.Ta, -bit),
                    d => WNAEAnimator.AddAdd(d, ctx.Acc, bit));
            }

            return current;
        }

        /// <summary>ta と tb を同時に崩し、ビットごとの論理演算結果を acc へ積む。</summary>
        private static List<VirtualState> BuildBitwise(
            CalcContext ctx, List<VirtualState> current, WNAECalcOperation operation)
        {
            for (var k = ctx.BitWidth - 1; k >= 0; k--)
            {
                var bit = 1 << k;
                ctx.Column++;

                var next = new List<VirtualState>();

                // (a, b) の 4 通り。最後の (0,0) を条件なしの受け皿にする
                var cases = new[]
                {
                    new { A = true, B = true, Row = 0 },
                    new { A = true, B = false, Row = 1 },
                    new { A = false, B = true, Row = 2 },
                    new { A = false, B = false, Row = 3 },
                };

                foreach (var c in cases)
                {
                    var aBit = c.A;
                    var bBit = c.B;
                    var result = Apply(operation, aBit, bBit);

                    var state = ctx.NewState($"b{k}={(aBit ? 1 : 0)}{(bBit ? 1 : 0)}", c.Row, d =>
                    {
                        if (aBit) WNAEAnimator.AddAdd(d, ctx.Ta, -bit);
                        if (bBit) WNAEAnimator.AddAdd(d, ctx.Tb, -bit);
                        if (result) WNAEAnimator.AddAdd(d, ctx.Acc, bit);
                    });

                    Link(current, state, new[]
                    {
                        aBit ? BitSet(ctx.Ta, bit) : BitClear(ctx.Ta, bit),
                        bBit ? BitSet(ctx.Tb, bit) : BitClear(ctx.Tb, bit),
                    });
                    next.Add(state);
                }

                current = next;
            }

            return current;
        }

        private static bool Apply(WNAECalcOperation operation, bool a, bool b)
        {
            switch (operation)
            {
                case WNAECalcOperation.And: return a && b;
                case WNAECalcOperation.Or: return a || b;
                case WNAECalcOperation.Xor: return a ^ b;
                case WNAECalcOperation.Xnor: return a == b;
                default: return false;
            }
        }

        /// <summary>
        /// c = a * b。tb の値ごとに分岐し、各分岐では定数倍（Convert Range）で一気に求める。
        /// 定数倍は除算を含まないため結果が厳密な整数になり、Int への丸め規則に依存しない。
        /// </summary>
        private static List<VirtualState> BuildMul(CalcContext ctx, List<VirtualState> current)
        {
            var modulus = ctx.Modulus;

            ctx.Column++;
            var branches = new List<VirtualState>();

            for (var b = 0; b < modulus; b++)
            {
                var value = b;
                var state = ctx.NewState($"b={value}", value, value == 0
                    ? (Action<VRCAvatarParameterDriver>)null
                    : d => WNAEAnimator.AddCopyRange(d, ctx.Acc, ctx.Ta, 0f, 1f, 0f, value));

                Link(current, state,
                    new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, value) });
                branches.Add(state);
            }

            // tb が値域外だった場合の受け皿（acc は Init で 0 になっている）
            var fallback = ctx.NewState("b=out", modulus, null);
            LinkOutOfRange(current, fallback, ctx.Tb, 0, modulus - 1);
            branches.Add(fallback);

            // 積は最大 (2^N-1)^2 なので、bit 2N-1 … N を落として mod 2^N にする
            return StripHighBits(ctx, branches, 2 * ctx.BitWidth - 1, ctx.BitWidth);
        }

        /// <summary>c = (a &lt;&lt; b) &amp; mask。シフト量ごとに定数倍してから上位ビットを落とす。</summary>
        private static List<VirtualState> BuildShiftLeft(CalcContext ctx, List<VirtualState> current)
        {
            var n = ctx.BitWidth;

            ctx.Column++;
            var branches = new List<VirtualState>();

            for (var s = 0; s < n; s++)
            {
                var shift = 1 << s;
                var state = ctx.NewState($"s={s}", s,
                    d => WNAEAnimator.AddCopyRange(d, ctx.Acc, ctx.Ta, 0f, 1f, 0f, shift));

                Link(current, state, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, s) });
                branches.Add(state);
            }

            // シフト量がビット幅以上なら全ビットが押し出される（acc は 0 のまま）
            var fallback = ctx.NewState("s>=N", n, null);
            LinkOutOfRange(current, fallback, ctx.Tb, 0, n - 1);
            branches.Add(fallback);

            if (n <= 1) return branches;

            // 積は最大 (2^N-1)×2^(N-1) なので bit 2N-2 … N を落とす
            return StripHighBits(ctx, branches, 2 * n - 2, n);
        }

        /// <summary>
        /// c = a &gt;&gt; b。シフト量ごとに分岐し、ta を bit N-1 から s まで崩して
        /// acc に 2^(k-s) を積む。s 未満のビットは触らないので自然に切り捨てられる。
        /// </summary>
        private static List<VirtualState> BuildShiftRight(CalcContext ctx, List<VirtualState> current)
        {
            var n = ctx.BitWidth;

            ctx.Column++;
            var dispatchColumn = ctx.Column;
            var terminals = new List<VirtualState>();
            var maxColumn = ctx.Column;

            for (var s = 0; s < n; s++)
            {
                ctx.Column = dispatchColumn;

                var branch = ctx.NewState($"s={s}", s * 2, null);
                Link(current, branch, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, s) });

                var chain = new List<VirtualState> { branch };

                for (var k = n - 1; k >= s; k--)
                {
                    var bit = 1 << k;
                    var weight = 1 << (k - s);
                    chain = BitLevel(ctx, chain, ctx.Ta, k, s * 2,
                        d =>
                        {
                            WNAEAnimator.AddAdd(d, ctx.Ta, -bit);
                            WNAEAnimator.AddAdd(d, ctx.Acc, weight);
                        },
                        null);
                }

                terminals.AddRange(chain);
                maxColumn = Mathf.Max(maxColumn, ctx.Column);
            }

            ctx.Column = dispatchColumn;
            var fallback = ctx.NewState("s>=N", n * 2, null);
            LinkOutOfRange(current, fallback, ctx.Tb, 0, n - 1);
            terminals.Add(fallback);

            ctx.Column = maxColumn;
            return terminals;
        }

        /// <summary>
        /// c = a / b（Div）または a % b（Mod）。
        /// tb の値ごとに分岐すると除数が定数になるので、b×2^k による二分長除算に落とせる。
        /// 商が acc に、剰余が ta に残るため、同じチェーンで両方求まる。
        /// </summary>
        private static List<VirtualState> BuildDivMod(
            CalcContext ctx, List<VirtualState> current, bool modulo)
        {
            var modulus = ctx.Modulus;
            var max = modulus - 1;

            ctx.Column++;
            var dispatchColumn = ctx.Column;
            var terminals = new List<VirtualState>();
            var maxColumn = ctx.Column;
            var row = 0;

            for (var b = 1; b < modulus; b++)
            {
                ctx.Column = dispatchColumn;

                var branch = ctx.NewState($"b={b}", row, null);
                Link(current, branch, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Equals, ctx.Tb, b) });

                var chain = new List<VirtualState> { branch };

                // b×2^k が最大値を超えない範囲の k から始める
                var top = 0;
                while ((long)b << (top + 1) <= max) top++;

                for (var k = top; k >= 0; k--)
                {
                    var step = b << k;
                    var weight = 1 << k;

                    ctx.Column++;
                    var yes = ctx.NewState($"-{step}", row, d =>
                    {
                        WNAEAnimator.AddAdd(d, ctx.Ta, -step);
                        WNAEAnimator.AddAdd(d, ctx.Acc, weight);
                    });
                    var no = ctx.NewState("skip", row + 1, null);

                    Link(chain, yes,
                        new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, ctx.Ta, step - 1) });
                    Link(chain, no,
                        new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, ctx.Ta, step) });

                    chain = new List<VirtualState> { yes, no };
                }

                terminals.AddRange(chain);
                maxColumn = Mathf.Max(maxColumn, ctx.Column);
                row += 2;
            }

            // b = 0 と値域外はゼロ除算として c = 0 にする
            ctx.Column = dispatchColumn;
            var zero = ctx.NewState("b=0", row, d =>
            {
                WNAEAnimator.AddSet(d, ctx.Acc, 0f);
                if (modulo) WNAEAnimator.AddSet(d, ctx.Ta, 0f);
            });
            LinkOutOfRange(current, zero, ctx.Tb, 1, max);
            terminals.Add(zero);

            ctx.Column = maxColumn;
            return terminals;
        }

        #endregion
    }

    #endregion

    #region Inspector

    [CustomEditor(typeof(WNAE_ParameterCalculation))]
    internal class WNAE_ParameterCalculationInspector : UnityEditor.Editor
    {
        private static readonly string[] OperationLabels =
            Enum.GetValues(typeof(WNAECalcOperation))
                .Cast<WNAECalcOperation>()
                .Select(WNAECalc.DisplayName)
                .ToArray();

        private string[] _intParameters = Array.Empty<string>();
        private readonly AdvancedDropdownState _dropdownState = new AdvancedDropdownState();

        private void OnEnable()
        {
            _intParameters = CollectIntParameters();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var operationProp = serializedObject.FindProperty("operation");
            operationProp.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent("Operation"), operationProp.enumValueIndex, OperationLabels);

            var operation = (WNAECalcOperation)operationProp.enumValueIndex;

            DrawParameterField("A (入力)", serializedObject.FindProperty("parameterA"));

            using (new EditorGUI.DisabledScope(!WNAECalc.UsesB(operation)))
            {
                DrawParameterField("B (入力)", serializedObject.FindProperty("parameterB"));
            }

            DrawParameterField("C (出力)", serializedObject.FindProperty("parameterC"));

            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("bitWidth"),
                new GUIContent("Bit Width",
                    "演算のビット幅。生成される State 数を大きく左右するので、実際に使う値域に合わせて絞ってください。"));

            serializedObject.ApplyModifiedProperties();

            // Bit Width を決めるための唯一の判断材料なので、これだけは表示する
            var calculation = (WNAE_ParameterCalculation)target;
            EditorGUILayout.LabelField(
                $"生成 State 数: {WNAECalcExpander.EstimateStateCount(calculation)}",
                EditorStyles.miniLabel);

            foreach (var issue in WNAECalcExpander.Validate((WNAE_ParameterCalculation)target))
            {
                if (issue.Level != WNAEIssueLevel.Error) continue;
                EditorGUILayout.HelpBox(issue.Message, MessageType.Error);
            }

            if (GUILayout.Button("パラメータ一覧を再取得"))
            {
                _intParameters = CollectIntParameters();
            }
        }

        /// <summary>
        /// 現在のコントローラから Int パラメータを列挙して選ばせる。
        /// SMB からはアバターを辿れないため、コントローラ自身が唯一の情報源になる。
        /// 取得できない場合は手入力にフォールバックする。
        ///
        /// Popup ではなく AdvancedDropdown を使うのは、Popup が "/" を階層区切りとして解釈するため。
        /// VRChat のパラメータ名には "/" がよく含まれ、"Costume" と "Costume/Top" が同時にあると
        /// 片方が一覧から消えてしまう。
        /// </summary>
        private void DrawParameterField(string label, SerializedProperty property)
        {
            if (_intParameters.Length == 0)
            {
                EditorGUILayout.PropertyField(property, new GUIContent(label));
                return;
            }

            var fieldRect = EditorGUI.PrefixLabel(
                EditorGUILayout.GetControlRect(), new GUIContent(label));

            var current = property.stringValue;
            var content = new GUIContent(string.IsNullOrEmpty(current) ? "（未選択）" : current);

            if (!EditorGUI.DropdownButton(fieldRect, content, FocusType.Keyboard, EditorStyles.popup)) return;

            var candidates = _intParameters
                .Select(name => new WNAEParameterCatalog.Candidate { Name = name, Source = "" })
                .ToList();

            // ドロップダウンのコールバックは後で走るため、パスから引き直して安全に書き込む
            var propertyPath = property.propertyPath;
            var so = serializedObject;

            var dropdown = new WNAEParameterDropdown(_dropdownState, "Int パラメータ", candidates, selected =>
            {
                so.Update();
                var target = so.FindProperty(propertyPath);
                if (target == null) return;

                target.stringValue = selected;
                so.ApplyModifiedProperties();
            });

            dropdown.Show(fieldRect);
        }

        /// <summary>
        /// VRChat SDK の Parameter Driver エディタと同じく、開いている Animator ウィンドウから
        /// コントローラを取得する。GetWindow だとウィンドウを開いてフォーカスを奪うため、
        /// 既存インスタンスの検索にとどめている。
        /// </summary>
        private static string[] CollectIntParameters()
        {
            var toolType = Type.GetType("UnityEditor.Graphs.AnimatorControllerTool, UnityEditor.Graphs");
            if (toolType == null) return Array.Empty<string>();

            var windows = Resources.FindObjectsOfTypeAll(toolType);
            if (windows == null || windows.Length == 0) return Array.Empty<string>();

            var property = toolType.GetProperty("animatorController",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (property == null) return Array.Empty<string>();

            if (!(property.GetValue(windows[0], null) is AnimatorController controller))
            {
                return Array.Empty<string>();
            }

            return controller.parameters
                .Where(p => p.type == AnimatorControllerParameterType.Int)
                .Select(p => p.name)
                .ToArray();
        }
    }

    #endregion
}
