import json, random, sys, os
from datetime import datetime, timedelta, timezone
from decimal import Decimal, ROUND_HALF_UP
out = sys.argv[1]
def iso(t): return t.isoformat(timespec='milliseconds').replace('+00:00', '+00:00')
def D(x, q='0.1'): return Decimal(str(x)).quantize(Decimal(q), rounding=ROUND_HALF_UP)
def num(d): return json.loads(str(d))  # keep as JSON number
def line(o): return json.dumps(o, ensure_ascii=False, separators=(',',':'))
T0 = datetime(2026,11,10,10,0,0,tzinfo=timezone.utc)

def write(dirname, events):
    os.makedirs(os.path.join(out, dirname), exist_ok=True)
    with open(os.path.join(out, dirname, 'market.jsonl'), 'w') as f:
        for e in events: f.write(e + '\n')

def trade(sym, t, price, qty, side=None, src='kraken', delayed=0, received=None):
    o = {"$type":"trade","price":num(price),"quantity":num(qty)}
    if side: o["aggressorSide"]=side
    o.update({"symbol":sym,"source":src,"asOfUtc":iso(t),"receivedUtc":iso(received or t),"delayedMinutes":delayed,"quality":"Delayed" if delayed else "Realtime"})
    return line(o)

def book(sym, t, bids, asks, snap):
    return line({"$type":"book","isSnapshot":snap,"bids":[{"price":num(p),"quantity":num(q)} for p,q in bids],
                 "asks":[{"price":num(p),"quantity":num(q)} for p,q in asks],"symbol":sym,"source":"kraken","asOfUtc":iso(t),"receivedUtc":iso(t)})

# --- Krypto: BTC/EUR, 30 Minuten
rnd = random.Random(42)
ev = []
mid = Decimal('60000.0')
t = T0
ev.append(book('BTC/EUR', t, [(mid-5-10*i, D(0.1+0.15*i,'0.00000001')) for i in range(10)], [(mid+5+10*i, D(0.1+0.15*i,'0.00000001')) for i in range(10)], True))
shape = lambda k: Decimal(str(round(-600*(1 if 600<k<1100 else 0) * min(1,(k-600)/200 if k<800 else 1) , 1)))
for k in range(1, 1800):
    t = T0 + timedelta(seconds=k)
    drift = Decimal(str(round(rnd.gauss(0, 6), 1)))
    target = Decimal('60000') + shape(k) + Decimal(str(round(400*__import__('math').sin(k/300),1)))
    mid = D(mid + drift + (target-mid)/Decimal(20))
    if k % 2 == 0:
        q = D(rnd.uniform(0.01, 0.6), '0.00000001')
        side = 'Buy' if rnd.random() < 0.5 else 'Sell'
        px = mid + (5 if side=='Buy' else -5)
        ev.append(trade('BTC/EUR', t, px, q, side))
    if k % 5 == 0:
        ev.append(book('BTC/EUR', t, [(mid-5-10*i, D(0.1+0.15*i,'0.00000001')) for i in range(10)], [(mid+5+10*i, D(0.1+0.15*i,'0.00000001')) for i in range(10)], True))
    elif k % 5 == 3:
        ev.append(book('BTC/EUR', t, [(mid-5, D(rnd.uniform(0.05,0.4),'0.00000001'))], [(mid+5, D(rnd.uniform(0.05,0.4),'0.00000001'))], False))
write('crypto-btc', ev)

# --- Aktie verzögert: SAP (gettex), 15 Minuten Verzögerung, Daten 09:30–10:45 Marktzeit
rnd = random.Random(7)
ev = []
px = Decimal('180.00')
for k in range(0, 75*6):
    asof = T0 - timedelta(minutes=30) + timedelta(seconds=10*k)
    px = D(px + Decimal(str(round(rnd.gauss(0, 0.08), 2))) + (Decimal('-0.03') if 200 < k < 300 else Decimal('0.01')), '0.01')
    q = rnd.randint(5, 400)
    ev.append(trade('DE0007164600', asof, px, q, None, 'gettex', 15, asof + timedelta(minutes=15)))
write('stock-delayed', ev)

# --- Lücke: BTC/EUR Trades bis 10:05, Standby, Kerzen 10:05–10:35 nachgeladen, danach weiter
rnd = random.Random(99)
ev = []
mid = Decimal('60000')
for k in range(0, 300, 3):
    t = T0 + timedelta(seconds=k)
    mid = D(mid + Decimal(str(round(rnd.gauss(0, 4), 1))))
    ev.append(trade('BTC/EUR', t, mid, D(rnd.uniform(0.01,0.5),'0.00000001')))
candles = []
o = mid
for m in range(5, 35):
    start = T0 + timedelta(minutes=m)
    move = Decimal(str(round(rnd.gauss(-60 if m < 20 else 50, 80), 1)))
    if m == 12: o = o - 500   # Kurssprung
    c = o + move
    h = max(o, c) + D(rnd.uniform(5, 60)); l = min(o, c) - D(rnd.uniform(5, 60))
    candles.append({"symbol":"BTC/EUR","timeframe":"M1","openTimeUtc":iso(start),"open":num(D(o)),"high":num(D(h)),"low":num(D(l)),"close":num(D(c)),"volume":num(D(rnd.uniform(2,8),'0.0001')),"tradeCount":rnd.randint(50,300),"source":"kraken","isBackfill":True,"isClosed":True})
    o = c
gap_t = T0 + timedelta(minutes=35)
ev.append(line({"$type":"gap","gapStartUtc":iso(T0+timedelta(minutes=5)),"gapEndUtc":iso(gap_t),"backfilledCandles":candles,"symbol":"BTC/EUR","source":"kraken","asOfUtc":iso(gap_t),"receivedUtc":iso(gap_t),"quality":"Backfill"}))
mid = D(o)
for k in range(0, 120, 3):
    t = gap_t + timedelta(seconds=k+1)
    mid = D(mid + Decimal(str(round(rnd.gauss(0, 4), 1))))
    ev.append(trade('BTC/EUR', t, mid, D(rnd.uniform(0.01,0.5),'0.00000001')))
write('gap-standby', ev)
