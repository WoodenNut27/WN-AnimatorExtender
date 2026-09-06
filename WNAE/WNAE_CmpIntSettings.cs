using System.Collections.Generic;
using UnityEngine;

namespace WoodenNut.WNAE
{
    /// <summary>
    /// アバタールートに付けて CmpInt を宣言するコンポーネント。
    /// Int と生成 Bool は全プレイアブルレイヤーのコントローラに宣言され、
    /// Int から Bool への変換レイヤーは FX にのみ生成される。
    /// IEditorOnly なのでビルド時に自動的に取り除かれる。
    ///
    /// MonoBehaviour は Unity の制約でクラス名とファイル名を一致させる必要があるため、
    /// データ型（<see cref="CmpIntEntry"/> など）とは別ファイルにしている。
    /// </summary>
    [AddComponentMenu("WoodenNut/WNAE CmpInt Settings")]
    [DisallowMultipleComponent]
    public class WNAE_CmpIntSettings : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        public List<CmpIntEntry> entries = new List<CmpIntEntry>();
    }
}
