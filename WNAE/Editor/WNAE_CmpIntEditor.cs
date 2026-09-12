using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using ReorderableList = UnityEditorInternal.ReorderableList;

namespace WoodenNut.WNAE
{
    #region Resolved

    /// <summary>
    /// 1 個の CmpInt について、継承 / 上書きを解決した最終的な設定と、検出時に出た指摘をまとめたもの。
    /// </summary>
    internal class ResolvedCmpInt : IWNAEResolved
    {
        public CmpIntEntry Entry;
        public CmpIntRange Range;

        /// <summary>上書き設定または既存 VRCExpressionParameters から解決した Default。</summary>
        public int DefaultValue;

        /// <summary>上書き設定または既存 VRCExpressionParameters から解決した Saved。</summary>
        public bool Saved;

        /// <summary>継承元となる既存の Expression Parameter。無ければ null。</summary>
        public VRCExpressionParameters.Parameter Existing;

        public List<WNAEIssue> Issues { get; } = new List<WNAEIssue>();

        public string ParameterName => Entry != null ? Entry.name : "";
        public int BitCount => Range.IsValid ? Range.BitCount : 0;
        public string BoolNameAt(int index) => Entry.BoolName(index);

        public bool IsNameCheckable =>
            Entry != null && !string.IsNullOrWhiteSpace(Entry.name) && Range.IsValid &&
            Range.ValueCount <= CmpIntExpander.MaxValueCount;
    }

    #endregion

    #region Range scanning

    /// <summary>
    /// Animator と Expressions メニューを走査して、CmpInt が実際に取り得る値域を求める。
    /// </summary>
    internal static class CmpIntRangeScanner
    {
        /// <summary>観測した値を集めて値域にまとめる。</summary>
        private class Collector
        {
            private readonly string _parameter;
            private bool _any;
            private int _min;
            private int _max;

            /// <summary>Greater / Less を見た。真の上限・下限までは分からない。</summary>
            public bool SawInequality { get; private set; }

            /// <summary>Add や範囲指定なし Copy を見た。静的に値域が決まらない。</summary>
            public readonly HashSet<string> UnboundedWrites = new HashSet<string>();

            public Collector(string parameter)
            {
                _parameter = parameter;
            }

            public bool HasAny => _any;
            public CmpIntRange Range => _any ? new CmpIntRange(_min, _max) : new CmpIntRange(0, -1);

            public void Observe(int value)
            {
                if (!_any)
                {
                    _any = true;
                    _min = _max = value;
                    return;
                }

                if (value < _min) _min = value;
                if (value > _max) _max = value;
            }

            public void Observe(float value) => Observe(Mathf.RoundToInt(value));

            public void ObserveConditions(IEnumerable<AnimatorCondition> conditions)
            {
                if (conditions == null) return;

                foreach (var condition in conditions)
                {
                    if (condition.parameter != _parameter) continue;

                    var threshold = Mathf.RoundToInt(condition.threshold);

                    switch (condition.mode)
                    {
                        case AnimatorConditionMode.Equals:
                        case AnimatorConditionMode.NotEqual:
                            Observe(threshold);
                            break;

                        // Greater/Less は境界しか分からない。少なくとも境界の隣は到達し得る
                        case AnimatorConditionMode.Greater:
                            Observe(threshold);
                            if (threshold < int.MaxValue) Observe(threshold + 1);
                            SawInequality = true;
                            break;

                        case AnimatorConditionMode.Less:
                            Observe(threshold);
                            if (threshold > int.MinValue) Observe(threshold - 1);
                            SawInequality = true;
                            break;
                    }
                }
            }

            public void ObserveBehaviours(IEnumerable<StateMachineBehaviour> behaviours)
            {
                if (behaviours == null) return;

                foreach (var driver in behaviours.OfType<VRCAvatarParameterDriver>())
                {
                    if (driver.parameters == null) continue;

                    foreach (var p in driver.parameters)
                    {
                        if (p == null || p.name != _parameter) continue;

                        switch (p.type)
                        {
                            case VRC_AvatarParameterDriver.ChangeType.Set:
                                Observe(p.value);
                                break;

                            case VRC_AvatarParameterDriver.ChangeType.Random:
                                Observe(p.valueMin);
                                Observe(p.valueMax);
                                break;

                            case VRC_AvatarParameterDriver.ChangeType.Copy:
                                if (p.convertRange)
                                {
                                    Observe(p.destMin);
                                    Observe(p.destMax);
                                }
                                else
                                {
                                    UnboundedWrites.Add("Copy（Convert Range 未使用）");
                                }
                                break;

                            case VRC_AvatarParameterDriver.ChangeType.Add:
                                UnboundedWrites.Add("Add");
                                break;
                        }
                    }
                }
            }

            /// <summary>
            /// メニューの Toggle / Button はパラメータに値を直接書き込むので、確実な値の情報源になる。
            /// Radial などの subParameters は Float 用なので見ない。
            /// </summary>
            public void ObserveMenuControl(VRCExpressionsMenu.Control control)
            {
                if (control.parameter == null || control.parameter.name != _parameter) return;

                Observe(control.value);
                // Toggle / Button / SubMenu / Puppet の主パラメータは、非アクティブ時に 0 へ戻る。
                // Default が 0 とは限らないため、明示的に観測する。
                Observe(0);
            }

            public void ObserveBlendTreeThresholds(string blendParameter, string blendParameterY,
                IEnumerable<float> thresholds)
            {
                if (blendParameter != _parameter && blendParameterY != _parameter) return;

                foreach (var threshold in thresholds) Observe(threshold);
            }
        }

        /// <summary>ビルド時。マージ済みの全コントローラとメニューを走査する。</summary>
        public static ResolvedCmpInt Resolve(
            CmpIntEntry entry,
            IEnumerable<VirtualAnimatorController> controllers,
            VRCExpressionsMenu menu,
            VRCExpressionParameters parameters)
        {
            return Resolve(entry, parameters, collector =>
            {
                foreach (var controller in controllers)
                {
                    if (controller == null) continue;

                    foreach (var layer in controller.Layers)
                    {
                        Walk(layer.StateMachine, collector);
                    }
                }

                WalkMenu(menu, collector, new HashSet<VRCExpressionsMenu>());
            });
        }

        /// <summary>Inspector 用。オーサリング時のアバターをベストエフォートで走査する。</summary>
        public static ResolvedCmpInt Resolve(CmpIntEntry entry, VRCAvatarDescriptor descriptor)
        {
            var parameters = descriptor != null ? descriptor.expressionParameters : null;

            return Resolve(entry, parameters, collector =>
            {
                if (descriptor == null) return;

                foreach (var layer in EnumerateCustomLayers(descriptor))
                {
                    if (!(layer.animatorController is AnimatorController controller)) continue;

                    foreach (var animatorLayer in controller.layers)
                    {
                        Walk(animatorLayer.stateMachine, collector);
                    }
                }

                WalkMenu(descriptor.expressionsMenu, collector, new HashSet<VRCExpressionsMenu>());
            });
        }

        private static ResolvedCmpInt Resolve(
            CmpIntEntry entry, VRCExpressionParameters parameters, Action<Collector> scan)
        {
            var resolved = new ResolvedCmpInt { Entry = entry };

            // 継承元となる既存のパラメータ
            resolved.Existing = parameters == null || parameters.parameters == null
                ? null
                : parameters.parameters.FirstOrDefault(p => p != null && p.name == entry.name);

            // Default / Saved は「上書き」か「既存から継承」かを先に確定させる
            resolved.DefaultValue = entry.overrideDefaultValue
                ? entry.defaultValue
                : resolved.Existing != null ? Mathf.RoundToInt(resolved.Existing.defaultValue) : 0;

            resolved.Saved = entry.overrideSaved
                ? entry.saved
                : resolved.Existing != null && resolved.Existing.saved;

            if (!entry.overrideDefaultValue && resolved.Existing == null)
            {
                resolved.Issues.Add(new WNAEIssue(WNAEIssueLevel.Info,
                    "継承元の Expression Parameter が無いため、Default は 0 / Saved は OFF になります。"));
            }

            if (entry.rangeOverride)
            {
                resolved.Range = new CmpIntRange(entry.minValue, entry.maxValue);

                if (!resolved.Range.IsValid)
                {
                    resolved.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                        $"Max ({entry.maxValue}) は Min ({entry.minValue}) 以上にしてください。"));
                }
                else if (resolved.DefaultValue < entry.minValue || resolved.DefaultValue > entry.maxValue)
                {
                    resolved.Issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                        $"Default ({resolved.DefaultValue}) が指定した値域外です。" +
                        $"ビルド時に {resolved.Range.Clamp(resolved.DefaultValue)} へ丸められます。"));
                }

                return resolved;
            }

            var collector = new Collector(entry.name);

            // Default は必ず表現できる必要がある
            collector.Observe(resolved.DefaultValue);
            scan(collector);

            resolved.Range = collector.Range;

            if (!collector.HasAny || !resolved.Range.IsValid)
            {
                resolved.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    "この Int を使っている値が Animator にもメニューにも見つかりませんでした。" +
                    "Range Override で値域を手入力してください。"));
                return resolved;
            }

            if (collector.SawInequality)
            {
                resolved.Issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                    $"Greater / Less の条件があるため真の値域が確定できません。検出結果は {resolved.Range} です。" +
                    "これで正しいか確認し、違う場合は Range Override を使ってください。"));
            }

            if (collector.UnboundedWrites.Count > 0)
            {
                resolved.Issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                    $"値域を静的に決められない Parameter Driver（{string.Join(" / ", collector.UnboundedWrites)}）が" +
                    $"あります。検出結果は {resolved.Range} です。範囲外の値を書き込む場合は Range Override を使ってください。"));
            }

            return resolved;
        }

        private static IEnumerable<VRCAvatarDescriptor.CustomAnimLayer> EnumerateCustomLayers(
            VRCAvatarDescriptor descriptor)
        {
            foreach (var layer in descriptor.baseAnimationLayers ??
                                  Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
            {
                yield return layer;
            }

            foreach (var layer in descriptor.specialAnimationLayers ??
                                  Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
            {
                yield return layer;
            }
        }

        private static void WalkMenu(
            VRCExpressionsMenu menu, Collector collector, HashSet<VRCExpressionsMenu> visited)
        {
            if (menu == null || menu.controls == null || !visited.Add(menu)) return;

            foreach (var control in menu.controls)
            {
                if (control == null) continue;

                collector.ObserveMenuControl(control);
                WalkMenu(control.subMenu, collector, visited);
            }
        }

        #region VirtualAnimatorController の走査（ビルド時）

        private static void Walk(VirtualStateMachine stateMachine, Collector collector)
        {
            if (stateMachine == null) return;

            foreach (var transition in stateMachine.AnyStateTransitions)
            {
                collector.ObserveConditions(transition.Conditions);
            }

            foreach (var transition in stateMachine.EntryTransitions)
            {
                collector.ObserveConditions(transition.Conditions);
            }

            foreach (var kv in stateMachine.StateMachineTransitions)
            {
                foreach (var transition in kv.Value) collector.ObserveConditions(transition.Conditions);
            }

            collector.ObserveBehaviours(stateMachine.Behaviours);

            foreach (var child in stateMachine.States)
            {
                var state = child.State;
                if (state == null) continue;

                foreach (var transition in state.Transitions) collector.ObserveConditions(transition.Conditions);
                collector.ObserveBehaviours(state.Behaviours);
                Walk(state.Motion, collector);
            }

            foreach (var child in stateMachine.StateMachines)
            {
                Walk(child.StateMachine, collector);
            }
        }

        private static void Walk(VirtualMotion motion, Collector collector)
        {
            if (!(motion is VirtualBlendTree tree)) return;

            collector.ObserveBlendTreeThresholds(
                tree.BlendParameter, tree.BlendParameterY, tree.Children.Select(c => c.Threshold));

            foreach (var child in tree.Children) Walk(child.Motion, collector);
        }

        #endregion

        #region AnimatorController の走査（Inspector 用）

        private static void Walk(AnimatorStateMachine stateMachine, Collector collector)
        {
            if (stateMachine == null) return;

            foreach (var transition in stateMachine.anyStateTransitions)
            {
                collector.ObserveConditions(transition.conditions);
            }

            foreach (var transition in stateMachine.entryTransitions)
            {
                collector.ObserveConditions(transition.conditions);
            }

            collector.ObserveBehaviours(stateMachine.behaviours);

            foreach (var child in stateMachine.states)
            {
                var state = child.state;
                if (state == null) continue;

                foreach (var transition in state.transitions) collector.ObserveConditions(transition.conditions);
                collector.ObserveBehaviours(state.behaviours);
                Walk(state.motion, collector);
            }

            foreach (var child in stateMachine.stateMachines)
            {
                foreach (var transition in stateMachine.GetStateMachineTransitions(child.stateMachine))
                {
                    collector.ObserveConditions(transition.conditions);
                }

                Walk(child.stateMachine, collector);
            }
        }

        private static void Walk(Motion motion, Collector collector)
        {
            if (!(motion is BlendTree tree)) return;

            collector.ObserveBlendTreeThresholds(
                tree.blendParameter, tree.blendParameterY, tree.children.Select(c => c.threshold));

            foreach (var child in tree.children) Walk(child.motion, collector);
        }

        #endregion
    }

    #endregion

    #region Expander

    internal static class CmpIntExpander
    {
        /// <summary>これを超える値数は警告（生成 State 数が膨らむ）。</summary>
        public const int WarnValueCount = 64;

        /// <summary>VRChat の同期 Int の上限に合わせた値数の上限。</summary>
        public const int MaxValueCount = 256;

        public static string DescribeResult(ResolvedCmpInt item)
        {
            return $"値域 {item.Range} を検出 ({item.Range.ValueCount} 値 / {item.Range.BitCount} bit" +
                   $"{(item.Entry.rangeOverride ? " / 手動指定" : "")})";
        }

        /// <summary>ビルド時の解決。名前の衝突検査は呼び出し側が CmpFloat とまとめて行う。</summary>
        public static List<ResolvedCmpInt> Resolve(
            IEnumerable<WNAE_CmpIntSettings> components,
            IEnumerable<VirtualAnimatorController> controllers,
            VRCAvatarDescriptor descriptor)
        {
            var controllerList = controllers.ToList();

            var resolved = components
                .Where(c => c != null && c.entries != null)
                .SelectMany(c => c.entries)
                .Where(e => e != null)
                .Select(e => CmpIntRangeScanner.Resolve(
                    e, controllerList, descriptor.expressionsMenu, descriptor.expressionParameters))
                .ToList();

            foreach (var item in resolved) Validate(item);

            return resolved;
        }

        /// <summary>値域そのものに関する検証。名前の重複・衝突は WNAENameValidator が担当する。</summary>
        public static void Validate(ResolvedCmpInt item)
        {
            var entry = item.Entry;

            if (entry == null)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "エントリが空です。"));
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.name))
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error, "パラメータ名が空です。"));
                return;
            }

            if (!item.Range.IsValid) return;

            var valueCount = item.Range.ValueCount;

            if (valueCount > MaxValueCount)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Error,
                    $"値の個数が {valueCount} 個で上限 {MaxValueCount} を超えています。"));
                return;
            }

            if (valueCount > WarnValueCount)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                    $"値の個数が {valueCount} 個です。エンコード/デコード合わせて約 {valueCount * 2} State 生成されます。" +
                    "ビルド時間と Animator のサイズに影響します。"));
            }

            if (item.Range.SavedBits <= 0)
            {
                item.Issues.Add(new WNAEIssue(WNAEIssueLevel.Warning,
                    $"必要ビット数が {item.Range.BitCount} bit で、通常の同期 Int " +
                    $"({WNAEUtil.UncompressedCost} bit) より節約になりません。"));
            }
        }

        public static void ApplyExpressionParameters(
            WNAEExpressionParameters parameters, List<ResolvedCmpInt> items)
        {
            foreach (var item in items)
            {
                var entry = item.Entry;
                var defaultValue = item.Range.Clamp(item.DefaultValue);
                var defaultIndex = item.Range.IndexOf(defaultValue);

                // CmpInt 本体: Synced のチェック状態に関わらず非同期化する
                var intParam = parameters.GetOrAdd(entry.name);
                intParam.valueType = VRCExpressionParameters.ValueType.Int;
                intParam.networkSynced = false;
                intParam.saved = item.Saved;
                intParam.defaultValue = defaultValue;

                for (var bit = 0; bit < item.Range.BitCount; bit++)
                {
                    parameters.SetSyncedBool(entry.BoolName(bit), WNAEUtil.GetBit(defaultIndex, bit));
                }
            }
        }

        /// <summary>元の Int と生成 Bool 群をコントローラに宣言する。</summary>
        public static void DeclareParameters(
            VirtualAnimatorController controller, List<ResolvedCmpInt> items)
        {
            foreach (var item in items)
            {
                var entry = item.Entry;
                var defaultValue = item.Range.Clamp(item.DefaultValue);

                controller.SetParameter(entry.name, new AnimatorControllerParameter
                {
                    name = entry.name,
                    type = AnimatorControllerParameterType.Int,
                    defaultInt = defaultValue,
                });

                var defaultIndex = item.Range.IndexOf(defaultValue);
                for (var bit = 0; bit < item.Range.BitCount; bit++)
                {
                    WNAEAnimator.EnsureBoolParameter(
                        controller, entry.BoolName(bit), WNAEUtil.GetBit(defaultIndex, bit));
                }
            }
        }

        public static void BuildLayers(
            VirtualAnimatorController fx,
            List<ResolvedCmpInt> items,
            VirtualClip emptyClip,
            bool writeDefaults)
        {
            foreach (var item in items)
            {
                BuildEncodeLayer(fx, item, emptyClip, writeDefaults);
                BuildDecodeLayer(fx, item, emptyClip, writeDefaults);
            }
        }

        /// <summary>ローカルで Int を読み、同期 Bool 群へ書き出すレイヤー。</summary>
        private static void BuildEncodeLayer(
            VirtualAnimatorController controller,
            ResolvedCmpInt item,
            VirtualClip emptyClip,
            bool writeDefaults)
        {
            var entry = item.Entry;
            var stateMachine = WNAEAnimator.CreateConversionLayer(
                controller, "Enc/" + entry.name, emptyClip, writeDefaults);

            var transitions = new List<VirtualStateTransition>();
            VirtualState first = null;
            VirtualState last = null;

            for (var offset = 0; offset < item.Range.ValueCount; offset++)
            {
                var value = (int)((long)item.Range.Min + offset);
                var index = item.Range.IndexOf(value);

                var state = stateMachine.AddState($"={value}", emptyClip, new Vector3(320f, index * 55f, 0f));
                state.WriteDefaultValues = writeDefaults;

                // 同期 Bool への書き込みはローカルのみ
                var driver = WNAEAnimator.CreateDriver($"WNAE Enc {entry.name} = {value}", localOnly: true);
                for (var bit = 0; bit < item.Range.BitCount; bit++)
                {
                    WNAEAnimator.AddSet(driver, entry.BoolName(bit), WNAEUtil.GetBit(index, bit) ? 1f : 0f);
                }

                state.Behaviours = state.Behaviours.Add(driver);

                transitions.Add(WNAEAnimator.CreateAnyStateTransition(state, new[]
                {
                    WNAEAnimator.Condition(AnimatorConditionMode.If, WNAEAnimator.IsLocalParameter, 0f),
                    WNAEAnimator.Condition(AnimatorConditionMode.Equals, entry.name, value),
                }));

                first = first ?? state;
                last = state;
            }

            // 値域外の入力は端へクランプする。受け皿が無いとどの遷移も成立せず、
            // 生成 Bool が直前の値のまま固まってリモートとの不一致が解消されない。
            // CmpFloat は最下段・最上段が受け皿になっているので、意味論をそろえる。
            if (first != null)
            {
                transitions.Add(WNAEAnimator.CreateAnyStateTransition(first, new[]
                {
                    WNAEAnimator.Condition(AnimatorConditionMode.If, WNAEAnimator.IsLocalParameter, 0f),
                    WNAEAnimator.Condition(AnimatorConditionMode.Less, entry.name, item.Range.Min),
                }));

                transitions.Add(WNAEAnimator.CreateAnyStateTransition(last, new[]
                {
                    WNAEAnimator.Condition(AnimatorConditionMode.If, WNAEAnimator.IsLocalParameter, 0f),
                    WNAEAnimator.Condition(AnimatorConditionMode.Greater, entry.name, item.Range.Max),
                }));
            }

            stateMachine.AnyStateTransitions = ImmutableList.CreateRange(transitions);
        }

        /// <summary>同期 Bool 群を読み、Int へ戻すレイヤー。</summary>
        private static void BuildDecodeLayer(
            VirtualAnimatorController controller,
            ResolvedCmpInt item,
            VirtualClip emptyClip,
            bool writeDefaults)
        {
            var entry = item.Entry;
            var stateMachine = WNAEAnimator.CreateConversionLayer(
                controller, "Dec/" + entry.name, emptyClip, writeDefaults);

            var transitions = new List<VirtualStateTransition>();

            for (var offset = 0; offset < item.Range.ValueCount; offset++)
            {
                var value = (int)((long)item.Range.Min + offset);
                var index = item.Range.IndexOf(value);

                var state = stateMachine.AddState($"={value}", emptyClip, new Vector3(320f, index * 55f, 0f));
                state.WriteDefaultValues = writeDefaults;

                // 書き込み先は非同期 Int なので、リモートでも実行させる
                var driver = WNAEAnimator.CreateDriver($"WNAE Dec {entry.name} = {value}", localOnly: false);
                WNAEAnimator.AddSet(driver, entry.name, value);
                state.Behaviours = state.Behaviours.Add(driver);

                // Bool -> Int はリモートでのみ行う（ローカルはエンコード側が唯一の書き手）
                var conditions = new List<AnimatorCondition>
                {
                    WNAEAnimator.Condition(AnimatorConditionMode.IfNot, WNAEAnimator.IsLocalParameter, 0f),
                };

                for (var bit = 0; bit < item.Range.BitCount; bit++)
                {
                    conditions.Add(WNAEAnimator.Condition(
                        WNAEUtil.GetBit(index, bit) ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                        entry.BoolName(bit),
                        0f));
                }

                transitions.Add(WNAEAnimator.CreateAnyStateTransition(state, conditions));
            }

            stateMachine.AnyStateTransitions = ImmutableList.CreateRange(transitions);
        }
    }

    #endregion

    #region Inspector

    [CustomEditor(typeof(WNAE_CmpIntSettings))]
    internal class WNAE_CmpIntSettingsEditor : WNAESettingsEditor
    {
        private List<ResolvedCmpInt> _resolved = new List<ResolvedCmpInt>();

        /// <summary>Options を開いたときの Default / Saved / Range / Bool Prefix。</summary>
        private const int OptionRows = 4;

        protected override string ListHeader => "CmpInt パラメータ";
        protected override string ParameterKindLabel => "Int パラメータ";
        protected override AnimatorControllerParameterType AnimatorType => AnimatorControllerParameterType.Int;
        protected override VRCExpressionParameters.ValueType ExpressionType => VRCExpressionParameters.ValueType.Int;

        protected override void RefreshResolved(VRCAvatarDescriptor descriptor)
        {
            var settings = (WNAE_CmpIntSettings)target;
            var entries = settings.entries ?? new List<CmpIntEntry>();

            _resolved = entries
                .Select(e =>
                {
                    if (e == null) return new ResolvedCmpInt { Entry = null };

                    var item = CmpIntRangeScanner.Resolve(e, descriptor);
                    CmpIntExpander.Validate(item);
                    return item;
                })
                .ToList();

            WNAENameValidator.Validate(
                _resolved.Cast<IWNAEResolved>().ToList(),
                WNAENameValidator.CollectExistingNames(descriptor));
        }

        private ResolvedCmpInt ResolvedAt(int index)
        {
            return index >= 0 && index < _resolved.Count ? _resolved[index] : null;
        }

        protected override void OnAddEntry(SerializedProperty element)
        {
            element.FindPropertyRelative("name").stringValue = "";
            element.FindPropertyRelative("overrideDefaultValue").boolValue = false;
            element.FindPropertyRelative("defaultValue").intValue = 0;
            element.FindPropertyRelative("overrideSaved").boolValue = false;
            element.FindPropertyRelative("saved").boolValue = true;
            element.FindPropertyRelative("rangeOverride").boolValue = false;
            element.FindPropertyRelative("minValue").intValue = 0;
            element.FindPropertyRelative("maxValue").intValue = 7;
            element.FindPropertyRelative("overrideBoolPrefix").boolValue = false;
            element.FindPropertyRelative("boolPrefixOverride").stringValue = "";
        }

        protected override float ElementHeight(int index)
        {
            var resolved = ResolvedAt(index);
            var entry = resolved?.Entry;

            var rows = AlwaysVisibleRows;

            if (index >= 0 && index < Entries.arraySize && Entries.GetArrayElementAtIndex(index).isExpanded)
            {
                rows += OptionRows;
                // Range を上書きするときだけ Min / Max の行が増える
                if (entry != null && entry.rangeOverride) rows++;
            }

            return rows * (Line + Pad) + Pad * 2 + ErrorsHeight(resolved);
        }

        protected override void DrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= Entries.arraySize) return;

            var element = Entries.GetArrayElementAtIndex(index);
            var resolved = ResolvedAt(index);

            var row = new Rect(rect.x, rect.y + Pad, rect.width, Line);

            Rect NextRow()
            {
                var r = row;
                row.y += Line + Pad;
                return r;
            }

            DrawNameField(NextRow(), element.FindPropertyRelative("name"));

            element.isExpanded = EditorGUI.Foldout(NextRow(), element.isExpanded, "Options", true);

            if (element.isExpanded) DrawOptions(element, resolved, NextRow);

            if (resolved != null) DrawErrors(resolved, ref row);
        }

        /// <summary>
        /// Options の中身。各項目は左端のチェックボックスで「既存設定を継承」か「上書き」を切り替える。
        /// </summary>
        private static void DrawOptions(SerializedProperty element, ResolvedCmpInt resolved, Func<Rect> nextRow)
        {
            var entry = resolved?.Entry;

            DrawOverrideRow(nextRow(), element.FindPropertyRelative("overrideDefaultValue"),
                new GUIContent("Default", "OFF のときは既存の Expression Parameter の Default をそのまま使います。"),
                resolved != null ? resolved.DefaultValue.ToString() : "-",
                (rect, label) => EditorGUI.PropertyField(
                    rect, element.FindPropertyRelative("defaultValue"), label));

            DrawOverrideRow(nextRow(), element.FindPropertyRelative("overrideSaved"),
                new GUIContent("Saved",
                    "Int の値をアバターに保存します。圧縮用の Bool は毎回 Animator が Set し直すため保存されません。\n" +
                    "OFF のときは既存の Expression Parameter の Saved をそのまま使います。"),
                resolved != null ? (resolved.Saved ? "ON" : "OFF") : "-",
                (rect, label) => EditorGUI.PropertyField(
                    rect, element.FindPropertyRelative("saved"), label));

            var rangeOverride = element.FindPropertyRelative("rangeOverride");
            DrawOverrideRow(nextRow(), rangeOverride,
                new GUIContent("Range",
                    "値域を手入力します。OSC など Animator から静的に検出できない書き込みがある場合に ON にしてください。"),
                resolved != null && resolved.Range.IsValid ? $"自動検出 {resolved.Range}" : "自動検出",
                (rect, label) => EditorGUI.LabelField(rect, label));

            if (rangeOverride.boolValue)
            {
                DrawMinMaxFields(Indent(nextRow()),
                    element.FindPropertyRelative("minValue"),
                    element.FindPropertyRelative("maxValue"));
            }

            DrawOverrideRow(nextRow(), element.FindPropertyRelative("overrideBoolPrefix"),
                new GUIContent("Bool Prefix", "OFF のときは \"{Name}_b\" が使われます。"),
                entry != null ? CmpIntUtil.BoolPrefix(entry) : "-",
                (rect, label) => EditorGUI.PropertyField(
                    rect, element.FindPropertyRelative("boolPrefixOverride"), label));
        }
    }

    #endregion
}
