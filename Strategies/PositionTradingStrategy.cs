using BotCripto.Models;

namespace BotCripto.Strategies;

// Position trading long-only su candele giornaliere (azioni italiane):
//  1) trend primario rialzista: Close > EMA50 > EMA200 e EMA50 in salita
//  2) pullback: nelle ultime candele il minimo ha toccato la zona EMA20/EMA50 e la candela chiude di nuovo sopra EMA20
//  3) ripartenza: candela rialzista, RSI14 in recupero (>45, in crescita) e non ipercomprato
//  4) filtro di mercato: FTSE MIB sopra la sua EMA50
//  5) forza relativa: il titolo deve aver battuto il FTSE MIB sul periodo
// Stop loss sotto il minimo dello swing recente (meno un buffer ATR); il target lo calcola il RiskManager (R:R configurato).
public class PositionTradingStrategy
{
    public const string Name = "Position Trading (Trend EMA50/200 + Pullback)";

    // Parametri: i valori di default sono quelli di produzione; BOT18_<NOME> li sovrascrive (solo per ottimizzazione).
    private static decimal P(string name, decimal def) =>
        decimal.TryParse(Environment.GetEnvironmentVariable("BOT18_" + name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;

    private static readonly int FastEma = (int)P("FAST_EMA", 20);
    private static readonly int MidEma = (int)P("MID_EMA", 50);
    private const int SlowEma = 200;
    private static readonly int MarketRegimeEmaPeriod = (int)P("MARKET_EMA", 50);
    private static readonly int PullbackLookback = (int)P("PULLBACK_LOOKBACK", 5);   // candele entro cui deve essere avvenuto il pullback
    private static readonly int SwingLookback = (int)P("SWING_LOOKBACK", 10);        // candele per il minimo dello swing (stop)
    private const int AtrPeriod = 14;
    private static readonly decimal AtrStopBuffer = P("ATR_BUFFER", 1.5m);           // buffer sotto lo swing, in ATR
    private static readonly decimal RsiMin = P("RSI_MIN", 45m);
    private static readonly decimal RsiMax = P("RSI_MAX", 65m);
    private static readonly decimal MaxStopPercent = P("MAX_STOP", 0.15m);           // stop più largo = setup scartato
    private static readonly decimal PullbackFloor = P("PULLBACK_FLOOR", 0.97m);      // minimo del pullback non oltre EMA50 * questo valore
    public const int MinCandles = SlowEma + 10;

    // Short abilitati (specchio dei long). Default: disattivati (variabile BOT18_SHORTS=1 per attivarli).
    public static bool AllowShort { get; set; } = Environment.GetEnvironmentVariable("BOT18_SHORTS") == "1";

    // Filtro forza relativa: long solo se il titolo ha battuto il FTSE MIB di almeno RsMin sul periodo, short solo se ha fatto peggio.
    private static readonly bool RsEnabled = P("RS_ENABLED", 1m) == 1m;
    private static readonly decimal RsMin = P("RS_MIN", 0m);
    public static readonly int RsPeriod = (int)P("RS_DAYS", 60);   // candele su cui si misura la forza relativa
    public static readonly bool RsSort = P("RS_SORT", 1m) == 1m;   // a parità di segnale entrano prima i più forti (long) / più deboli (short)

    // Regime di mercato: true se il FTSE MIB chiude sopra la sua EMA50 sull'ultima candela, false se sotto, null se dati insufficienti.
    public static bool? IsMarketBullish(List<Candle> marketCandles)
    {
        var ema = TechnicalIndicators.CalculateEMA(marketCandles.Select(c => c.Close).ToList(), MarketRegimeEmaPeriod);
        if (ema.Count == 0)
            return null;
        return marketCandles.Last().Close > ema.Last();
    }

    // Forza relativa = rendimento del titolo meno rendimento del FTSE MIB sulle ultime RsPeriod candele (allineate per data).
    public static decimal? CalculateRelativeStrength(List<Candle> candles, List<Candle> marketCandles)
    {
        if (candles.Count <= RsPeriod || marketCandles.Count == 0)
            return null;
        var now = candles[^1];
        var prev = candles[candles.Count - 1 - RsPeriod];
        var mktNow = marketCandles.FirstOrDefault(c => c.Time == now.Time);
        var mktPrev = marketCandles.FirstOrDefault(c => c.Time == prev.Time);
        if (mktNow == null || mktPrev == null || prev.Close <= 0 || mktPrev.Close <= 0)
            return null;
        return now.Close / prev.Close - mktNow.Close / mktPrev.Close;
    }

    // relStrength = rendimento del titolo meno rendimento del FTSE MIB sul periodo (null = non disponibile)
    public AnalysisResult Analyze(string symbol, List<Candle> candles, bool? marketBullish, decimal? relStrength = null)
    {
        var res = AnalyzeSide(symbol, candles, marketBullish, relStrength, isShort: false);
        if (res.IsSignal || !AllowShort || res.Rejection != RejectionReason.TrendNotAligned)
            return res;
        var shortRes = AnalyzeSide(symbol, candles, marketBullish, relStrength, isShort: true);
        return shortRes.IsSignal ? shortRes : res;
    }

    private AnalysisResult AnalyzeSide(string symbol, List<Candle> candles, bool? marketBullish, decimal? relStrength, bool isShort)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = Name,
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < MinCandles)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var closes = candles.Select(c => c.Close).ToList();
        var ema20 = TechnicalIndicators.CalculateEMA(closes, FastEma);
        var ema50 = TechnicalIndicators.CalculateEMA(closes, MidEma);
        var ema200 = TechnicalIndicators.CalculateEMA(closes, SlowEma);
        var rsi = TechnicalIndicators.CalculateRSI(closes);
        if (ema20.Count == 0 || ema50.Count < 6 || ema200.Count == 0 || rsi.Count < 2)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var last = candles.Last();
        result.CurrentPrice = last.Close;

        // Le serie EMA sono allineate alla fine: l'indice i-esimo dalla fine corrisponde alla candela i-esima dalla fine
        decimal E20(int back) => ema20[ema20.Count - 1 - back];
        decimal E50(int back) => ema50[ema50.Count - 1 - back];

        var e20 = E20(0);
        var e50 = E50(0);
        var e200 = ema200.Last();
        result.Indicators["EMA20"] = e20;
        result.Indicators["EMA50"] = e50;
        result.Indicators["EMA200"] = e200;

        // 1) Trend primario (long: rialzista; short: ribassista, specchio esatto)
        bool trend = isShort
            ? last.Close < e50 && e50 < e200 && e50 < E50(5)
            : last.Close > e50 && e50 > e200 && e50 > E50(5);
        if (!trend)
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        // 2) Pullback recente verso la zona EMA20/EMA50 e ripresa oltre EMA20
        bool pullback = false;
        for (int back = 1; back <= PullbackLookback; back++)
        {
            var c = candles[candles.Count - 1 - back];
            bool touched = isShort
                ? c.High >= E20(back) && c.High <= E50(back) * (2m - PullbackFloor)
                : c.Low <= E20(back) && c.Low >= E50(back) * PullbackFloor;
            if (touched)
            {
                pullback = true;
                break;
            }
        }
        if (!pullback || (isShort ? last.Close >= e20 : last.Close <= e20))
        {
            result.Rejection = RejectionReason.NoBreakout;
            return result;
        }

        // 3) Ripartenza: candela concorde e RSI in direzione ma non estremo (short: RSI specchiato 100 - x)
        var rsiNow = rsi.Last();
        var rsiPrev = rsi[rsi.Count - 2];
        result.Indicators["RSI"] = rsiNow;
        bool rsiOk = isShort
            ? rsiNow >= 100m - RsiMax && rsiNow <= 100m - RsiMin && rsiNow < rsiPrev
            : rsiNow >= RsiMin && rsiNow <= RsiMax && rsiNow > rsiPrev;
        bool candleOk = isShort ? last.Close < last.Open : last.Close > last.Open;
        if (!candleOk || !rsiOk)
        {
            result.Rejection = RejectionReason.RsiExtreme;
            return result;
        }

        // 4) Regime FTSE MIB (long: sopra EMA, short: sotto)
        if (marketBullish != !isShort)
        {
            result.Rejection = RejectionReason.AgainstMarketRegime;
            return result;
        }

        // 5) Forza relativa vs FTSE MIB
        if (RsEnabled)
        {
            if (relStrength == null || (isShort ? relStrength > -RsMin : relStrength < RsMin))
            {
                result.Rejection = RejectionReason.AgainstMarketRegime;
                return result;
            }
            result.Indicators["RelStrength"] = relStrength.Value;
        }

        // Stop oltre lo swing recente (long: sotto il minimo, short: sopra il massimo), con buffer ATR
        var atr = CalculateAtr(candles, AtrPeriod);
        var recent = candles.Skip(candles.Count - SwingLookback);
        decimal stop = isShort ? recent.Max(c => c.High) + atr * AtrStopBuffer : recent.Min(c => c.Low) - atr * AtrStopBuffer;
        bool stopBad = isShort
            ? stop <= last.Close || (stop - last.Close) / last.Close > MaxStopPercent
            : stop <= 0 || stop >= last.Close || (last.Close - stop) / last.Close > MaxStopPercent;
        if (stopBad)
        {
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }

        result.IsSignal = true;
        result.Signal = isShort ? "SELL" : "BUY";
        result.Indicators["StopLoss"] = stop;
        result.Indicators["ATR"] = atr;
        // Più forte = trend più marcato (distanza EMA50 da EMA200) e RSI più estremo nel range consentito
        result.Indicators["SignalStrength"] = isShort
            ? (e200 - e50) / e200 * 100m + ((100m - RsiMin) - rsiNow) / 10m
            : (e50 - e200) / e200 * 100m + (rsiNow - RsiMin) / 10m;
        return result;
    }

    private static decimal CalculateAtr(List<Candle> candles, int period)
    {
        var trs = new List<decimal>();
        for (int i = candles.Count - period; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            trs.Add(Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose))));
        }
        return trs.Average();
    }
}
