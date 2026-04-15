# cs-editorconfig

[AndanteTribe](https://github.com/AndanteTribe) 組織共通の `.editorconfig` および `.gitattributes` を管理するリポジトリです。

## 概要

このリポジトリには以下が含まれます。

| ファイル | 説明 |
|---|---|
| `.editorconfig` | C# プロジェクト向けの共通コーディングスタイル設定 |
| `.gitattributes` | 行末の正規化設定 |

---

## GitHub App を使った自動配布（推奨）

`main` ブランチに変更がプッシュされると、**GitHub App** が組織内のインストール済みリポジトリに対して自動的に PR を作成します。  
各リポジトリで個別に CI を用意する必要はありません。

### セットアップ手順

#### 1. GitHub App を作成する

[GitHub の App 作成ページ](https://github.com/organizations/AndanteTribe/settings/apps/new) で以下の設定で App を作成します。

| 項目 | 値 |
|---|---|
| GitHub App name | 任意（例: `cs-editorconfig-distributor`） |
| Homepage URL | `https://github.com/AndanteTribe/cs-editorconfig` |
| Webhook | 無効 (Unchecked) |

**Repository permissions:**

| Permission | Access |
|---|---|
| Contents | Read & write |
| Pull requests | Read & write |

作成後、**App ID** と **秘密鍵（Private key）** を控えておきます。

#### 2. App をインストールする

作成した GitHub App をリポジトリまたは組織全体にインストールします。  
`https://github.com/organizations/AndanteTribe/settings/apps/<app-name>/installations` から操作します。

#### 3. Secrets を登録する

`cs-editorconfig` リポジトリの **Settings → Secrets and variables → Actions** に以下の Repository secrets を追加します。

| Secret 名 | 値 |
|---|---|
| `APP_ID` | GitHub App の App ID |
| `PRIVATE_KEY` | 生成した秘密鍵（`.pem` ファイルの内容） |

> **Tip:** 組織内の複数リポジトリで同じ App を使用する場合は、Organization secrets として登録すると管理が楽になります。

#### 4. 動作確認

`.editorconfig` または `.gitattributes` を変更して `main` にプッシュすると、[`distribute.yml`](./.github/workflows/distribute.yml) ワークフローが起動し、App がインストールされている各リポジトリに対して PR が作成されます。

---

## 手動更新（旧来の方法）

App をインストールしない場合や、既存リポジトリで一時的に更新を行いたい場合は、各リポジトリの **Actions → Update EditorConfig** から手動でワークフローを実行できます。

### 対象リポジトリへの初回導入

```bash
git remote add upstream-cs-editorconfig https://github.com/AndanteTribe/cs-editorconfig.git
git fetch upstream-cs-editorconfig
git merge upstream-cs-editorconfig/main --allow-unrelated-histories
```

これにより `.editorconfig`, `.gitattributes`, `.github/workflows/update-stream.yml` が導入されます。

### 以降の更新

導入した [`update-stream.yml`](./.github/workflows/update-stream.yml) ワークフローを手動実行することで更新 PR を作成できます。
