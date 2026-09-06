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
    #region Chain building blocks

    internal struct WNAEBuildResult
    {
        /// <summary>チェーンの入口。ここへ元 State の入遷移が張り替えられる。</summary>
        public VirtualState First;

        /// <summary>
        /// チェーンの終端。ここへ元 State の出遷移が移される。
        /// 複数の State に同じ遷移インスタンスを共有させないため、終端は必ず 1 個にする。
        /// </summary>
        public VirtualState Last;
    }

    /// <summary>展開中の Sub State Machine と、そこで使う中間パラメータをまとめたもの。</summary>
    internal class WNAEChainContext
    {
        public const float ColumnWidth = 240f;
        public const float RowHeight = 55f;

        public VirtualStateMachine StateMachine;
        public VirtualClip Clip;
        public bool WriteDefaults;

        /// <summary>中間パラメータ名の接頭辞。展開単位で一意。</summary>
        public string Prefix;

        public int Column;
        public int BitWidth = WNAECodec.BitCount;

        public string Ta => Prefix + "/ta";
        public string Tb => Prefix + "/tb";
        public string Acc => Prefix + "/acc";
        public string T2 => Prefix + "/t2";

        public int Modulus => 1 << BitWidth;

        public VirtualState NewState(string label, int row, Action<VRCAvatarParameterDriver> fill)
        {
            var state = StateMachine.AddState(
                label, Clip, new Vector3(Column * ColumnWidth, row * RowHeight, 0f));
            state.WriteDefaultValues = WriteDefaults;

            if (fill == null) return state;

            var driver = WNAEAnimator.CreateDriver($"WNAE {label}", localOnly: false);
            fill(driver);
            if (driver.parameters.Count > 0) state.Behaviours = state.Behaviours.Add(driver);

            return state;
        }
    }

    /// <summary>State を数珠つなぎにするための共通部品。</summary>
    internal static class WNAEChain
    {
        public static void Link(
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
        public static void LinkOutOfRange(
            IEnumerable<VirtualState> from, VirtualState to, string parameter, long min, long max)
        {
            var states = from as IList<VirtualState> ?? from.ToList();

            Link(states, to, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Less, parameter, min) });
            Link(states, to, new[] { WNAEAnimator.Condition(AnimatorConditionMode.Greater, parameter, max) });
        }

        public static AnimatorCondition BitSet(string parameter, int bit)
        {
            return WNAEAnimator.Condition(AnimatorConditionMode.Greater, parameter, bit - 1);
        }

        public static AnimatorCondition BitClear(string parameter, int bit)
        {
            return WNAEAnimator.Condition(AnimatorConditionMode.Less, parameter, bit);
        }

        /// <summary>
        /// value を「最上位の残っている単位」から直接消していく分解段を張る。
        ///
        /// レベル k の State へは「stepOf(k) &lt;= value &lt; stepOf(k+1)」の 2 条件で、
        /// それまでの全 State から直接跳び込む。中間に何もしない受け State を挟まないため、
        /// 素朴な 2 分岐チェーンに比べて State 数が半分になり、所要フレームも
        /// 「立っているビットの数」だけで済む。
        ///
        /// stepOf は 1 レベル上がるごとにちょうど 2 倍になること（2^k、b×2^k など）。
        /// そうでないと「レベル k で引いた残りが必ずレベル k 未満に収まる」前提が崩れる。
        ///
        /// 戻り値は入口を含む全 State。呼び出し元は「value &lt; stepOf(lo)」を完了条件として
        /// 全戻り State から次へ繋ぐこと。また、入口で value が stepOf(hi+1) 以上になり得る場合は
        /// 「value &gt; stepOf(hi+1)-1」の受け皿も入口に張り、行き場のない State を作らないこと。
        /// </summary>
        public static List<VirtualState> GreedyDispatch(
            WNAEChainContext ctx,
            IEnumerable<VirtualState> entry,
            string value,
            int hi,
            int lo,
            int row,
            Func<int, long> stepOf,
            Func<int, string> labelOf,
            Func<int, Action<VRCAvatarParameterDriver>> driverOf)
        {
            var sources = new List<VirtualState>(entry);

            for (var k = hi; k >= lo; k--)
            {
                ctx.Column++;
                var state = ctx.NewState(labelOf(k), row, driverOf(k));

                Link(sources, state, new[]
                {
                    WNAEAnimator.Condition(AnimatorConditionMode.Greater, value, stepOf(k) - 1),
                    WNAEAnimator.Condition(AnimatorConditionMode.Less, value, stepOf(k + 1)),
                });

                sources.Add(state);
            }

            return sources;
        }

        /// <summary>Bool パラメータで分岐する 2 分岐を 1 段追加する。</summary>
        public static List<VirtualState> BoolLevel(
            WNAEChainContext ctx,
            List<VirtualState> previous,
            string parameter,
            string label,
            int row,
            Action<VRCAvatarParameterDriver> onTrue,
            Action<VRCAvatarParameterDriver> onFalse)
        {
            ctx.Column++;

            var on = ctx.NewState($"{label}=1", row, onTrue);
            var off = ctx.NewState($"{label}=0", row + 1, onFalse);

            Link(previous, on, new[] { WNAEAnimator.Condition(AnimatorConditionMode.If, parameter, 0f) });
            Link(previous, off, new[] { WNAEAnimator.Condition(AnimatorConditionMode.IfNot, parameter, 0f) });

            return new List<VirtualState> { on, off };
        }
    }

    #endregion

    #region Builder interface

    /// <summary>
    /// State に置かれた Behaviour 1 個を、Sub State Machine 内のチェーンへ展開する役割。
    /// State の差し替えや遷移の張り替えは <see cref="WNAEBehaviourExpander"/> が共通で行う。
    /// </summary>
    internal interface IWNAEBehaviourBuilder
    {
        /// <summary>ログ表示用の名前。</summary>
        string DisplayName { get; }

        /// <summary>中間パラメータ名の接頭辞。</summary>
        string ParameterPrefix { get; }

        /// <summary>生成する Sub State Machine 名の接頭辞。</summary>
        string SubStateMachinePrefix { get; }

        bool Matches(StateMachineBehaviour behaviour);

        List<WNAEIssue> Validate(StateMachineBehaviour behaviour);

        /// <summary>使用するパラメータをコントローラに宣言する。</summary>
        void Prepare(
            VirtualAnimatorController controller, WNAEChainContext ctx, StateMachineBehaviour behaviour);

        WNAEBuildResult Build(WNAEChainContext ctx, StateMachineBehaviour behaviour);
    }

    #endregion

    #region Expander

    /// <summary>
    /// WNAE の StateMachineBehaviour が置かれた State を、
    /// Parameter Driver と遷移条件だけで構成された Sub State Machine へ置き換える。
    /// </summary>
    internal static class WNAEBehaviourExpander
    {
        private static readonly IWNAEBehaviourBuilder[] Builders =
        {
            WNAECalcBuilder.Instance,
            WNAEEncoderBuilder.Instance,
            WNAEDecoderBuilder.Instance,
        };

        private struct Target
        {
            public VirtualStateMachine Parent;
            public VirtualState State;
            public Vector3 Position;
            public List<StateMachineBehaviour> Behaviours;
        }

        public static IWNAEBehaviourBuilder BuilderFor(StateMachineBehaviour behaviour)
        {
            if (behaviour == null) return null;

            foreach (var builder in Builders)
            {
                if (builder.Matches(behaviour)) return builder;
            }

            return null;
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
                        clip = clip ?? VirtualClip.Create("WNAE Expand Empty");
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

                var behaviours = state.Behaviours.Where(b => BuilderFor(b) != null).ToList();
                if (behaviours.Count == 0) continue;

                var label = $"{controller.Name} / {state.Name}";

                // SMB はユーザーのコントローラアセットの実体なので、リストから外すだけにする
                state.Behaviours = state.Behaviours.RemoveAll(b => BuilderFor(b) != null);

                var accepted = new List<StateMachineBehaviour>();
                foreach (var behaviour in behaviours)
                {
                    var builder = BuilderFor(behaviour);
                    var issues = builder.Validate(behaviour);

                    foreach (var issue in issues)
                    {
                        var message = $"[WNAE] {builder.DisplayName} ({label}): {issue.Message}";
                        if (issue.Level == WNAEIssueLevel.Error) UnityEngine.Debug.LogError(message);
                        else if (issue.Level == WNAEIssueLevel.Warning) UnityEngine.Debug.LogWarning(message);
                        else UnityEngine.Debug.Log(message);
                    }

                    if (issues.Any(i => i.Level == WNAEIssueLevel.Error)) continue;
                    accepted.Add(behaviour);
                }

                // 全部エラーなら State は差し替えられないので、Motion 喪失の警告も出さない
                if (accepted.Count == 0) continue;

                if (state.Motion != null || !state.Behaviours.IsEmpty)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[WNAE] {label}: この State は Sub State Machine に置き換えられるため、" +
                        "Motion と他の Behaviour は失われます。");
                }

                targets.Add(new Target
                {
                    Parent = stateMachine,
                    State = state,
                    Position = child.Position,
                    Behaviours = accepted,
                });
            }
        }

        /// <summary>元の State を、生成したチェーンを収めた Sub State Machine で置き換える。</summary>
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

            var namePrefix = BuilderFor(target.Behaviours[0]).SubStateMachinePrefix;
            var sub = VirtualStateMachine.Create(controllerContext.CloneContext, namePrefix + state.Name);

            parent.StateMachines = parent.StateMachines.Add(new VirtualStateMachine.VirtualChildStateMachine
            {
                StateMachine = sub,
                Position = target.Position,
            });

            VirtualState head = null;
            VirtualState last = null;

            foreach (var behaviour in target.Behaviours)
            {
                var builder = BuilderFor(behaviour);

                var ctx = new WNAEChainContext
                {
                    StateMachine = sub,
                    Clip = clip,
                    WriteDefaults = state.WriteDefaultValues,
                    Prefix = builder.ParameterPrefix + index++,
                };

                builder.Prepare(controller, ctx, behaviour);

                var entry = builder.Build(ctx, behaviour);

                if (head == null)
                {
                    head = entry.First;
                    sub.DefaultState = head;
                }
                else
                {
                    // 直前の Behaviour の終端から、この Behaviour の先頭へ繋ぐ
                    WNAEChain.Link(new[] { last }, entry.First, null);
                }

                last = entry.Last;
            }

            if (head == null) return;

            // 出ていく遷移は終端へ移す。自己遷移が含まれていても、この後の張り替えで先頭に向く
            var outgoing = state.Transitions;
            state.Transitions = ImmutableList<VirtualStateTransition>.Empty;
            last.Transitions = outgoing;

            // Exit 行きの遷移は移設後 Sub State Machine の Exit までしか届かない。
            // 親側に Exit をそのまま伝播する遷移を足して、元の「親から抜ける」意味を保つ
            if (outgoing.Any(t => t != null && t.IsExit))
            {
                var exitTransition = VirtualTransition.Create();
                exitTransition.SetExitDestination();
                parent.StateMachineTransitions = parent.StateMachineTransitions.SetItem(
                    sub, ImmutableList.Create(exitTransition));
            }

            // 元の State を指していた遷移をすべて先頭 State へ向ける。
            // Sub State Machine 自体ではなく中の State を直接指すことで、
            // AnyState から Sub State Machine を指せるかという不確実性を避けている。
            Repoint(layerRoot, state, head);

            if (parent.DefaultState == state)
            {
                // Default State は Sub State Machine 内の State を指せないため、Entry から入れる。
                // 条件なしの Entry 遷移は常に成立するので、既存の条件付き Entry 遷移を
                // 遮らないよう末尾に置く（Default State の「どれにも該当しないとき」の座を引き継ぐ）
                var entryTransition = VirtualTransition.Create();
                entryTransition.SetDestination(sub);
                parent.EntryTransitions = parent.EntryTransitions.Add(entryTransition);

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

        /// <summary>
        /// 使用するパラメータをコントローラに宣言する。
        /// 既に別の型で存在する場合は、黙って解釈が変わらないよう警告を出す。
        /// </summary>
        public static void DeclareParameter(
            VirtualAnimatorController controller,
            string name,
            AnimatorControllerParameterType type,
            string context)
        {
            if (string.IsNullOrEmpty(name)) return;

            if (controller.Parameters.TryGetValue(name, out var existing))
            {
                if (existing.type != type)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[WNAE] {context}: \"{name}\" は {existing.type} 型です。" +
                        $"{type} として扱われるため、値が意図せず変換される可能性があります。");
                }
                return;
            }

            if (type == AnimatorControllerParameterType.Int)
            {
                WNAEAnimator.EnsureIntParameter(controller, name);
            }
            else
            {
                WNAEAnimator.EnsureBoolParameter(controller, name, false);
            }
        }
    }

    #endregion

    #region Inspector helpers

    /// <summary>
    /// StateMachineBehaviour からはアバターを辿れないため、開いている Animator ウィンドウの
    /// コントローラをパラメータ一覧の取得元にする（VRChat SDK の Parameter Driver と同じ方式）。
    /// </summary>
    internal static class WNAEAnimatorWindow
    {
        public static string[] CollectParameters(AnimatorControllerParameterType type)
        {
            var controller = CurrentController();
            if (controller == null) return Array.Empty<string>();

            return controller.parameters
                .Where(p => p.type == type)
                .Select(p => p.name)
                .ToArray();
        }

        private static AnimatorController CurrentController()
        {
            var toolType = Type.GetType("UnityEditor.Graphs.AnimatorControllerTool, UnityEditor.Graphs");
            if (toolType == null) return null;

            // GetWindow だとウィンドウを開いてフォーカスを奪うため、既存インスタンスの検索にとどめる
            var windows = Resources.FindObjectsOfTypeAll(toolType);
            if (windows == null || windows.Length == 0) return null;

            var property = toolType.GetProperty("animatorController",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            return property?.GetValue(windows[0], null) as AnimatorController;
        }
    }

    /// <summary>ラベルの一覧から番号で選ばせるドロップダウン。</summary>
    internal class WNAEIndexedDropdown : AdvancedDropdown
    {
        private readonly string _title;
        private readonly IReadOnlyList<string> _labels;
        private readonly Action<int> _onSelected;

        public WNAEIndexedDropdown(
            AdvancedDropdownState state, string title, IReadOnlyList<string> labels, Action<int> onSelected)
            : base(state)
        {
            _title = title;
            _labels = labels;
            _onSelected = onSelected;
            minimumSize = new Vector2(260f, 320f);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem(_title);

            for (var i = 0; i < _labels.Count; i++)
            {
                root.AddChild(new AdvancedDropdownItem(_labels[i]) { id = i });
            }

            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (item.id >= 0 && item.id < _labels.Count) _onSelected(item.id);
        }
    }

    /// <summary>
    /// WNAE の StateMachineBehaviour に共通する Inspector の部品。
    /// Animator ウィンドウからのパラメータ収集と、選択専用ドロップダウンを提供する。
    /// </summary>
    internal abstract class WNAEBehaviourInspector : UnityEditor.Editor
    {
        protected string[] IntParameters = Array.Empty<string>();
        protected string[] BoolParameters = Array.Empty<string>();

        private readonly AdvancedDropdownState _dropdownState = new AdvancedDropdownState();

        protected virtual void OnEnable() => RefreshParameters();

        protected void RefreshParameters()
        {
            IntParameters = WNAEAnimatorWindow.CollectParameters(AnimatorControllerParameterType.Int);
            BoolParameters = WNAEAnimatorWindow.CollectParameters(AnimatorControllerParameterType.Bool);
        }

        protected void DrawRefreshButton()
        {
            if (GUILayout.Button("パラメータ一覧を再取得")) RefreshParameters();
        }

        protected void DrawErrors(IEnumerable<WNAEIssue> issues)
        {
            foreach (var issue in issues)
            {
                if (issue.Level != WNAEIssueLevel.Error) continue;
                EditorGUILayout.HelpBox(issue.Message, MessageType.Error);
            }
        }

        /// <summary>
        /// ラベル一覧から選ばせるドロップダウンを 1 行描く。
        /// Popup ではなく AdvancedDropdown を使うのは、Popup が "/" を階層区切りとして
        /// 解釈してしまい、VRChat でよくある "Costume/Top" のような名前が壊れるため。
        /// </summary>
        protected void DrawDropdownRow(
            string label, string currentLabel, IReadOnlyList<string> labels, Action<int> onSelected)
        {
            var fieldRect = EditorGUI.PrefixLabel(
                EditorGUILayout.GetControlRect(), new GUIContent(label));

            if (!EditorGUI.DropdownButton(
                    fieldRect, new GUIContent(currentLabel), FocusType.Keyboard, EditorStyles.popup))
            {
                return;
            }

            var dropdown = new WNAEIndexedDropdown(_dropdownState, label, labels, onSelected);
            dropdown.Show(fieldRect);
        }

        /// <summary>パラメータ名を選ばせる行。一覧が取れないときは手入力にフォールバックする。</summary>
        protected void DrawParameterRow(
            string label, SerializedProperty property, string[] candidates, string emptyLabel)
        {
            if (candidates.Length == 0)
            {
                EditorGUILayout.PropertyField(property, new GUIContent(label));
                return;
            }

            var current = property.stringValue;
            var display = string.IsNullOrEmpty(current) ? emptyLabel : current;

            var labels = new List<string> { emptyLabel };
            labels.AddRange(candidates);

            // ドロップダウンのコールバックは後で走るため、パスから引き直して安全に書き込む
            var propertyPath = property.propertyPath;
            var so = serializedObject;

            DrawDropdownRow(label, display, labels, index =>
            {
                so.Update();
                var target = so.FindProperty(propertyPath);
                if (target == null) return;

                target.stringValue = index == 0 ? "" : labels[index];
                so.ApplyModifiedProperties();
            });
        }
    }

    #endregion
}
