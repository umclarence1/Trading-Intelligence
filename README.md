# Trading Automation Hub Demo

A small C# demo app that uses a live market API and a broker client interface.

## What it does
- reads live prices from Binance public API
- calculates a moving average
- generates `Buy`, `Sell`, or `Hold` signals
- sends simulated broker orders via a fake broker client
- prints live decisions and current position

## Run it
1. Open `TradingAutomationHub` in VS Code.
2. Open a terminal in that folder.
3. Run:

```bash
dotnet run -- BTCUSDT
```

4. Stop with `Ctrl+C`.

## Notes
- The app uses Binance's public symbol price endpoint: `https://api.binance.com/api/v3/ticker/price`.
- The broker client is currently simulated so the app is safe to run.
- You can replace `FakeBrokerClient` with a real broker API implementation later.

## Next steps
- add an actual broker API client for live orders
- add a position manager with risk controls
- add logging or charting output
- build a GUI with WinForms, WPF, or Blazor
