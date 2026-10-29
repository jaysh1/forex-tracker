<template>
  <section id="learn-panel" class="learn-panel" role="tabpanel" aria-labelledby="learn-tab" tabindex="0">
    <header class="learn-intro card">
      <p class="eyebrow">A friendly guide to the dashboard</p>
      <h2>How the Forex signal works</h2>
      <p>Think of the bot as a careful helper. It watches the price, waits for a few clues to line up, then asks you before placing any order.</p>
    </header>

    <article class="card lesson">
      <h3>EMA50 and EMA200: two smooth trails</h3>
      <p>An EMA is a line that smooths out price wiggles. “50” and “200” mean it reacts over different numbers of 15-minute candles. The EMA50 follows recent changes faster; EMA200 moves more slowly and shows the broader direction.</p>
      <figure class="chart-figure">
        <svg viewBox="0 0 520 220" role="img" aria-labelledby="ema-title ema-desc">
          <title id="ema-title">Illustration of price and two moving averages</title>
          <desc id="ema-desc">A wiggly price line rises. The faster EMA50 line follows it more closely and stays above the slower EMA200 line.</desc>
          <path class="chart-grid" d="M42 30H500M42 75H500M42 120H500M42 165H500M42 195H500" />
          <path class="chart-axis" d="M42 20V195H500" />
          <path class="price-line" d="M50 155L85 140L115 160L148 115L178 132L210 98L242 121L276 82L308 100L340 65L372 88L405 49L438 70L475 38" />
          <path class="ema-fast" d="M50 158C110 151 132 135 178 128S245 112 278 95S341 79 374 68S438 53 475 42" />
          <path class="ema-slow" d="M50 173C115 168 145 160 190 150S257 138 300 123S367 110 405 93S450 81 475 70" />
          <text x="48" y="214">Earlier</text><text x="443" y="214">More recent</text>
        </svg>
        <figcaption><span class="legend price-key">Price</span><span class="legend fast-key">EMA50 (faster)</span><span class="legend slow-key">EMA200 (slower)</span></figcaption>
      </figure>
      <p class="plain-note"><strong>Like explaining it to a child:</strong> imagine two dogs following a ball. The quick dog (EMA50) turns quickly; the older, slower dog (EMA200) takes longer. When the quick dog is above the slow dog, this strategy calls the trend “up.” That is a clue, not a promise that prices will rise.</p>
    </article>

    <article class="card lesson">
      <h3>RSI: a speedometer for recent price moves</h3>
      <p>RSI is a number from 0 to 100. Below 30 means prices have been falling strongly; above 70 means they have been rising strongly. This strategy waits for RSI to turn back out of those zones before considering an entry.</p>
      <figure class="rsi-figure">
        <svg viewBox="0 0 520 150" role="img" aria-labelledby="rsi-title rsi-desc">
          <title id="rsi-title">RSI scale from zero to one hundred</title>
          <desc id="rsi-desc">The buy trigger is a crossing upward through 30. The sell trigger is a crossing downward through 70. The example RSI near 52 is in the middle.</desc>
          <rect class="rsi-buy-zone" x="42" y="40" width="137" height="45" rx="8" />
          <rect class="rsi-middle-zone" x="179" y="40" width="274" height="45" />
          <rect class="rsi-sell-zone" x="453" y="40" width="45" height="45" rx="8" />
          <path class="rsi-track" d="M42 63H498" />
          <path class="rsi-marker" d="M280 34V92" />
          <circle class="rsi-dot" cx="280" cy="63" r="8" />
          <text x="37" y="112">0</text><text x="164" y="112">30</text><text x="266" y="112">52</text><text x="438" y="112">70</text><text x="485" y="112">100</text>
          <text x="58" y="30">Oversold</text><text x="414" y="30">Overbought</text>
        </svg>
        <figcaption>Buy clue: cross up through 30 · Sell clue: cross down through 70</figcaption>
      </figure>
      <p class="plain-note"><strong>Your message, translated:</strong> “RSI 52.9 → 52.3” means the speedometer stayed in the middle and dipped a little. It did not rise up through 30, so there was no buy trigger—even though the EMA trend was up.</p>
    </article>

    <article class="card lesson">
      <h3>What must happen before you see a proposal?</h3>
      <ol class="steps">
        <li><strong>Wait for a fresh, finished 5-minute candle.</strong> The scanner does not decide from a candle that is still forming.</li>
        <li><strong>Check the 15-minute direction.</strong> EMA50 above EMA200 allows a buy setup; EMA50 below EMA200 allows a sell setup.</li>
        <li><strong>Wait for the RSI crossing.</strong> Buy crosses up through 30; sell crosses down through 70.</li>
        <li><strong>Run the safety checks.</strong> Trading hours, current spread, news, open positions, account risk and order size must all pass.</li>
        <li><strong>Show you a proposal.</strong> You review it and approve it; the bot does not place the order on its own.</li>
      </ol>
      <p class="warn">No signal means no proposal. These rules can miss moves and do not predict what the market will do next.</p>
    </article>
  </section>
</template>
