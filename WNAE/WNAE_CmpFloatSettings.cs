using System.Collections.Generic;
using UnityEngine;

namespace WoodenNut.WNAE
{
    /// <summary>
    /// アバタールートに付けて CmpFloat を宣言するコンポーネント。
    /// Float と生成 Bool は全プレイアブルレイヤーのコントローラに宣言され、
    /// Float から Bool への変換レイヤーは FX にのみ生成される。
    /// IEditorOnly なのでビルド時に自動的に取り除かれる。
    ///
    /// MonoBehaviour は Unity の制約でクラス名とファイル名を一致させる必要があるため、
    /// データ型（<see cref="CmpFloatEntry"/> など）とは別ファイルにしている。
    /// </summary>
    [AddComponentMenu("WoodenNut/WNAE CmpFloat Settings")]
    [DisallowMultipleComponent]
    public class WNAE_CmpFloatSettings : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        public List<CmpFloatEntry> entries = new List<CmpFloatEntry>();
    }
}
