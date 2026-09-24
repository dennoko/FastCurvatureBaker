# Fast Curvature Baker 詳細設計

`overview.md` の要件を実装するための設計書。参考実装 `Assets/Editor/FastAOBaker` の構成（Services / Compute / Window 分離、非同期パイプライン、マテリアル単位出力）を踏襲しつつ、曲率計算アルゴリズムは一から設計し直す。

## 1. 要件の整理

| 項目 | 内容 |
| --- | --- |
| 対象 | Unity 2022.3.22f1 以降 / エディタ拡張 / Compute Shader 必須 |
| 入力 | 複数の GameObject（MeshRenderer + MeshFilter / SkinnedMeshRenderer） |
| 出力 | 曲率マップ PNG。レンダラーのサブメッシュ（=マテリアル）ごとに 1 枚 |
| 出力先 | マテリアルのベーステクスチャのあるフォルダ配下の `BakedCurvature/`。無ければ `Assets/BakedCurvature/`（Unity のプロジェクトルートに相当する、インポート可能な最上位フォルダ） |
| 品質/速度 | Substance Painter 並み（数秒以内・UV シームでの破綻なし・エッジが滑らか） |
| UI | ロジック検証用の簡易 UI（後でリプレイスする前提。ロジックは UI 非依存にする） |

## 2. 参考実装の曲率ベイクの問題点

FastAOBaker の `Curvature.compute` は UV 空間のポジション/法線マップを隣接テクセルで差分して曲率を求めていた（現在オミット中）。

1. **UV シームで破綻する**: テクスチャ上の隣接テクセルが 3D 上で隣接しているとは限らず、シーム検出のヒューリスティックに依存する。
2. **スケールが固定**: 差分幅が 1 テクセルなので「どの程度の太さのエッジを拾うか」を制御できず、解像度で見た目が変わる。
3. **遅い**: ラスタライズがテクセルごとに全三角形をループ（O(テクセル数 × 三角形数)）。

## 3. アルゴリズム

### 3.1 概要

**UV 空間 G-Buffer + 3D 近傍積分（integral invariant）による曲率推定**を採用する。

```
[CPU] メッシュ抽出 → 溶接・連結成分 → 表面サンプリング → 空間ハッシュ構築
[GPU] UV ラスタライズ(G-Buffer) → 曲率評価(タイル分割) → ダウンサンプル → ブラー → ダイレーション
[CPU] 値のマッピング → PNG 保存 → インポート設定
```

各テクセルについて、そのテクセルに対応する 3D 表面点 p を中心とした**半径 r の球内にある表面サンプル**を集計して曲率を求める。

- テクスチャ空間ではなく 3D 空間で近傍を取るため **UV シームの影響を原理的に受けない**。
- 半径 r（ワールド単位）が「曲率を見るスケール = エッジハイライトの太さ」になり、**解像度に依存しない**直感的なパラメータになる（Substance の Curvature の "radius" 相当）。
- 近傍サンプルの寄与に滑らかなカーネル重み `w = area · (1 − d²/r²)²` を掛けるため、結果が空間的に連続で**ノイズが少なく**、デノイズ処理が不要。

### 3.2 曲率の推定式

近傍サンプル q_i（位置、法線 n_i、面積重み a_i）に対して w_i = a_i (1 − |q_i − p|²/r²)² とする。

**(A) Shading Normals モード（既定）** — 法線場の変化から平均曲率を推定する。

```
κ_i = dot(n_i − n_p, q_i − p) / |q_i − p|²      （方向 q_i − p の法曲率）
H   = Σ w_i κ_i / Σ w_i                          （全方向平均 = 平均曲率）
```

球（半径 R・外向き法線）では全ての κ_i = 1/R となり厳密。メッシュの**描画用法線**を使うため、スムースシェーディングの低ポリゴンメッシュでもポリゴンの面が出ず、シェーディング通りの滑らかな曲率になる。ハードエッジ（法線分割）はエッジを挟んで法線がジャンプするため、幅 r のハイライトとして正しく検出される。

**(B) Geometry モード** — 形状そのもの（位置）から推定する。

```
c = Σ w_i q_i / Σ w_i                 （重み付き重心）
n̄ = normalize(Σ w_i n_i)             （近傍の平均法線）
H = 8 · dot(n̄, p − c) / r²
```

凸面では近傍が接平面の下にあり重心が p より内側に来る（dot > 0）。係数 8 はカーネル `(1 − d²/r²)²` の下で E[d²] = r²/4 となることから導かれ、球で H = 1/R となるよう正規化している。法線に依存しないため、ポリゴンの折れ（ファセット）もそのまま拾う（ハイポリや硬いプロップ向き）。

**出力値**: 無次元量 `v = H · r · Strength`（半径 r と同じ曲率半径の球で |v| = Strength）。

| OutputMode | 値 | 背景 |
| --- | --- | --- |
| Combined | `saturate(0.5 + 0.5·v)` 0.5=平坦, 白=凸, 黒=凹 | 0.5 |
| Convex | `saturate(v)` | 0 |
| Concave | `saturate(−v)` | 0 |

### 3.3 近傍の除外（誤検出対策）

VRChat アバターのように、服と素体・指同士など**別の面が近接している**ケースで、無関係な面を曲率に混ぜないための 2 段のフィルタ。

1. **連結成分フィルタ（既定 ON）**: 位置で溶接した頂点で Union-Find を行い、テクセルと同じ連結成分のサンプルだけを使う。UV シームで分割された頂点も溶接により同一成分になる。
2. **法線フィルタ**: `dot(n_i, n_p) < NormalRejection`（既定 −0.5 ≒ 120°）のサンプルを除外。薄い布の裏面など逆向きの面を除外する。

### 3.4 表面サンプリング（CPU）

- サンプル間隔 `s = r / SamplesPerRadius`（品質: Draft=4, Standard=6, High=8, Ultra=12）。円盤内サンプル数はおよそ π·SamplesPerRadius²（Standard で約 110）。
- 各三角形に `k = max(1, round(area / s²))` 個配置し、重み `area / k` を持たせる（小さな三角形も必ず 1 個持つので面積が正確に保存される）。
- 三角形内の配置は R2 低食い違い列（三角形ごとにハッシュでオフセット）を折り返して一様化。k=1 は重心。
- 総数が上限（2²² ≈ 420 万）を超える場合は s を広げる（警告ログ）。
- サンプル: `float3 pos, float3 normal(補間した描画法線), uint component, float weight` = 32 byte。
- サンプリングはメッシュ全体（全サブメッシュ）から行う。マテリアル境界をまたいでも曲率が連続になる。

### 3.5 空間ハッシュ（CPU 構築 / GPU 探索）

- セルサイズ `1.01·r`（CPU と GPU の floor 誤差があっても半径 r 以内のサンプルが必ず ±1 セルに入るよう 1% の余裕）。
- ハッシュ `(x·73856093) ^ (y·19349663) ^ (z·83492791)` を 2 の冪のテーブル（≥ サンプル数）でマスク。
- カウンティングソートでサンプルをバケット順に並べ、`uint2(start, count)` のテーブルを作る。
- GPU では 27 セルを走査。異なるセルが同じバケットに衝突した場合の二重カウントは、訪問済みバケット配列で除外する（距離判定は常に行うので、衝突による他セルのサンプル混入は無害）。

### 3.6 UV ラスタライズ（GPU）

- **1 スレッド = 1 三角形**で、UV 上のバウンディングボックス内テクセルを走査してエッジ関数で内外判定する。O(Σ 三角形の占有テクセル数) で、FastAOBaker の O(テクセル数 × 三角形数) から桁違いに速い。
- テクセル中心 (x+0.5)/res に対し、UV を `uv·res − 0.5` の画素空間に変換して判定。
- **2 パス**: ① 内部テクセルを書く → ② まだ空のテクセルのうち三角形から 0.75px 以内のものを、最近傍点の重心座標で書く（保守的ラスタライズ）。①を優先することで隣接する別アイランドへの滲みを防ぎつつ、アイランド境界の欠けを埋める。
- G-Buffer は StructuredBuffer（`float4(pos, component+1)` と 16bit×2 の八面体エンコード法線 `uint`）。RenderTexture を使わないので D3D/GL の上下反転問題が発生しない。インデックス `y·res + x` の y=0 が v=0（Texture2D の下端）に一致する。
- スーパーサンプリング（既定 ON）: 内部解像度を 2 倍にして評価し、有効テクセルのみで 2×2 平均する。内部解像度は最大 4096（メモリ上限のため、出力 4096 のときは自動で 1×）。

### 3.7 GPU 実行制御

- 曲率評価はテクセル範囲を分割してディスパッチし、各チャンク後に 8 byte の `AsyncGPUReadback.WaitForCompletion()` で完了を待つ。1 ディスパッチの実行時間を約 150ms に保つようチャンクサイズを適応的に調整する（Windows の TDR 2 秒制限回避・進捗表示・キャンセル対応）。
- 一定時間ごとに `await Task.Yield()` してエディタに制御を返す。

### 3.8 ポストプロセス（GPU）

1. **Downsample**: スーパーサンプリング時、有効テクセルのみの平均。
2. **Blur**（既定 0 回）: 有効テクセルのみの 3×3 ガウシアン × N 回。
3. **Dilation**（既定 16px）: 空テクセルを 8 近傍の有効テクセルの平均で埋める × N 回。ミップマップ時の UV 境界の滲み対策。

値のマッピング（3.2 の表）と背景色の適用は読み戻し後に CPU で行う。

## 4. モジュール構成

```
FastCurvatureBaker/
├── Docs/Impl/overview.md, design.md
└── Editor/
    ├── dennokoworks.FastCurvatureBaker.Editor.asmdef   (Editor 専用)
    ├── Core/                        (UI 非依存のロジック)
    │   ├── CurvatureBakeSettings.cs   設定値と列挙型、検証
    │   ├── SurfaceMesh.cs             ワールド空間メッシュデータ + 抽出/溶接/連結成分
    │   ├── SurfaceSampler.cs          表面サンプリング
    │   ├── SpatialHashGrid.cs         空間ハッシュ構築（HLSL と同一のハッシュ）
    │   ├── CurvatureGpu.cs            Compute Shader の実行（G-Buffer/評価/後処理/読み戻し）
    │   ├── CurvatureTextureExporter.cs 出力先解決・PNG 保存・インポート設定
    │   ├── MeshReadabilityUtility.cs  Read/Write 無効メッシュの検出と有効化
    │   └── CurvatureBakePipeline.cs   全体の進行管理（進捗・キャンセル・エラー集約）
    ├── Shaders/FastCurvatureBake.compute
    └── Window/FastCurvatureBakerWindow.cs  簡易 UI（メニュー: dennokoworks/Fast Curvature Baker）
```

### 4.1 公開 API（UI リプレイス時の接点）

```csharp
var pipeline = new CurvatureBakePipeline();
BakeReport report = await pipeline.RunAsync(
    IReadOnlyList<GameObject> targets,
    CurvatureBakeSettings settings,
    IProgress<BakeProgress> progress,     // (float Progress01, string Message)
    CancellationToken token);
// report.SavedAssetPaths / report.Errors
```

UI は設定の編集、`MeshReadabilityUtility` による事前確認、`RunAsync` の呼び出しと進捗表示のみを担当する。FastAOBaker 風の Store（単一方向データフロー）の UI に置き換える場合も Core は変更不要。

### 4.2 設定項目

| 設定 | 既定 | 説明 |
| --- | --- | --- |
| Resolution | 2048 | 出力解像度（256〜4096） |
| UVChannel | 0 | ベイクに使う UV（0〜7） |
| Radius | 0.01 (m) | 曲率を見る半径（ワールド単位）。エッジの太さ |
| Strength | 1.0 | 出力の強さ |
| Source | ShadingNormals | ShadingNormals / Geometry |
| OutputMode | Combined | Combined / Convex / Concave |
| Quality | Standard | 半径あたりのサンプル密度 |
| Supersample | true | 2×2 スーパーサンプリング |
| SameComponentOnly | true | 同一連結成分のみ使用 |
| NormalRejection | −0.5 | 法線の内積がこれ未満の近傍を除外 |
| BlurPasses | 0 | 仕上げのブラー回数 |
| DilationPixels | 16 | UV 外側への拡張ピクセル数 |
| OverwriteExisting | true | 同名ファイルを上書き（false で連番） |

## 5. メッシュ抽出

- **MeshRenderer**: `MeshFilter.sharedMesh` を `localToWorldMatrix` でワールド化。法線は逆転置行列で変換。
- **SkinnedMeshRenderer**: `BakeMesh(mesh, useScale: true)` で現在のポーズ・ブレンドシェイプを反映し、`TRS(position, rotation, 1)` でワールド化。UV・インデックスは元の `sharedMesh` から取得。
- 溶接は元メッシュのローカル座標の完全一致で行う（スキニング後の浮動小数誤差の影響を受けない）。
- 法線が無いメッシュは面法線の面積加重平均で補う。
- 三角形以外のトポロジのサブメッシュはスキップ。UV チャンネルが無い/頂点数と不一致ならそのレンダラーはエラー。
- Read/Write 無効のメッシュは、ベイク前にダイアログで ModelImporter の Read/Write 有効化を提案する。

## 6. 出力

- サブメッシュ i のマテリアル = `sharedMaterials[i]`。ベーステクスチャは `mainTexture` → `_BaseMap` → `_BaseColorMap` の順に探索し、`Assets/` 配下にあればその隣の `BakedCurvature/`、無ければ `Assets/BakedCurvature/`。
- ファイル名: `{GameObject名}[_{マテリアル名}]_Curvature.png`（サブメッシュが複数の場合にマテリアル名を付与。同一実行内での重複は `_sub{i}` で回避）。
- 8bit RGB PNG（グレースケール値）。インポート設定: sRGB オフ（線形データ）、非圧縮、maxSize = 解像度。

## 7. 計算量の目安

2048² 出力 + スーパーサンプリング（内部 4096² ≈ 1,670 万テクセル）、Standard 品質で 1 テクセルあたり候補約 300 サンプル → 約 50 億回の距離判定。ミドルレンジ GPU で数秒程度。Draft/スーパーサンプリング OFF で 1/8 以下になる。

## 8. 既知の制限

- UV が 0〜1 の外にある部分（UDIM 等）はクリップされる。
- 重なった UV（ミラー UV 等）は後から書いた三角形の値になる（AO 同様、UV を重ねたメッシュでは原理的に 1 つの値しか持てない）。
- 半径がサンプル上限に対して小さすぎる大規模メッシュでは、サンプル間隔が自動で広がりノイズが増える。
