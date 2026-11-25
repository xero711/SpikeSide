# SpikeSide Live Bridge

Overwolf の VALORANT ゲームイベントを、この PC 上の SpikeSide に渡す開発用コンパニオンです。ゲームイベントの対応状況は Overwolf とゲームのバージョンによって変わります。

## セットアップ

1. Overwolf を起動し、設定の「Support」→「Development options」を開きます。
2. 展開済みアプリを読み込む操作（Load unpacked extension）で、この `overwolf-live` フォルダーを選びます。
3. Overwolf から `SpikeSide Live Bridge` を起動します。VALORANT の起動時にも起動する設定です。
4. SpikeSide にログインし、「ライブ」を開きます。

SpikeSide は、この PC の `127.0.0.1` だけで待ち受けます。コンパニオンは `%LOCALAPPDATA%\ValoAutoPick\spikeside-overwolf-bridge.json` を読み、表示中の SpikeSide インスタンスにイベントを送ります。SpikeSide を再起動すると接続先が変わり、コンパニオンは自動で読み直します。イベントは外部サーバーへ送信しません。

試合中の K/D/A は Overwolf がスコアボードを提供し、参加者情報と照合できたときに表示されます。イベントが未取得・停止中の場合は `—` と表示します。実際の試合で対応するイベントが届くか確認してください。
