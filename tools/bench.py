import json
import time
import statistics
import concurrent.futures
import http.client

HOST = "localhost"
PORT = 9999
PATH = "/fraud-score"
PAYLOADS = [
    b'{"id":"tx-L1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}',
    b'{"id":"tx-F1","transaction":{"amount":4368.82,"installments":8,"requested_at":"2026-03-17T02:04:06Z"},"customer":{"avg_amount":68.88,"tx_count_24h":18,"known_merchants":["MERC-004","MERC-015","MERC-017","MERC-007"]},"merchant":{"id":"MERC-062","mcc":"7801","avg_amount":25.55},"terminal":{"is_online":true,"card_present":false,"km_from_home":881.61},"last_transaction":{"timestamp":"2026-03-17T01:58:06Z","km_from_current":660.92}}',
    b'{"id":"tx-L2","transaction":{"amount":120.00,"installments":1,"requested_at":"2026-04-01T10:00:00Z"},"customer":{"avg_amount":110.00,"tx_count_24h":1,"known_merchants":["MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5812","avg_amount":100.00},"terminal":{"is_online":true,"card_present":true,"km_from_home":2.5},"last_transaction":null}',
    b'{"id":"tx-B1","transaction":{"amount":500.00,"installments":3,"requested_at":"2026-05-10T15:30:00Z"},"customer":{"avg_amount":450.00,"tx_count_24h":5,"known_merchants":["MERC-010","MERC-020"]},"merchant":{"id":"MERC-030","mcc":"5999","avg_amount":480.00},"terminal":{"is_online":true,"card_present":false,"km_from_home":15.0},"last_transaction":{"timestamp":"2026-05-10T14:00:00Z","km_from_current":5.0}}'
]

class Worker:
    def __init__(self):
        self.conn = http.client.HTTPConnection(HOST, PORT)
        self.conn.connect()

    def request(self, payload):
        t0 = time.perf_counter()
        try:
            self.conn.request("POST", PATH, body=payload, headers={"Content-Type": "application/json"})
            resp = self.conn.getresponse()
            _ = resp.read()
            t1 = time.perf_counter()
            ok = resp.status == 200
            return (t1 - t0) * 1000, ok, resp.status
        except Exception as e:
            t1 = time.perf_counter()
            try:
                self.conn.close()
            except:
                pass
            self.conn = http.client.HTTPConnection(HOST, PORT)
            try:
                self.conn.connect()
            except:
                pass
            return (t1 - t0) * 1000, False, str(e)

def worker_loop(wid, total, concurrency, results, stop_event):
    worker = Worker()
    while not stop_event.is_set():
        try:
            idx = next(total)
        except StopIteration:
            break
        payload = PAYLOADS[idx % len(PAYLOADS)]
        lat, ok, status = worker.request(payload)
        results.append((lat, ok, status))

def run(total=5000, concurrency=50, warmup=500):
    import threading
    # Warmup
    w = Worker()
    for i in range(warmup):
        w.request(PAYLOADS[i % len(PAYLOADS)])
    w.conn.close()
    print(f"Warmup ({warmup} reqs) done. Running benchmark...")

    counter = iter(range(total))
    results = []
    stop_event = threading.Event()
    lock = threading.Lock()
    shared_results = []

    def worker_task():
        w = Worker()
        while True:
            try:
                idx = next(counter)
            except StopIteration:
                break
            payload = PAYLOADS[idx % len(PAYLOADS)]
            lat, ok, status = w.request(payload)
            shared_results.append((lat, ok, status))
        w.conn.close()

    start_all = time.perf_counter()
    threads = [threading.Thread(target=worker_task) for _ in range(concurrency)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    elapsed = time.perf_counter() - start_all

    latencies = [r[0] for r in shared_results]
    failures = [r for r in shared_results if not r[1]]
    latencies.sort()
    n = len(latencies)
    rps = round(n / elapsed, 1) if elapsed > 0 else 0

    def pct(p):
        i = int(n * p)
        if i >= n:
            i = n - 1
        return round(latencies[i], 3)

    avg = round(statistics.mean(latencies), 3) if n > 0 else 0
    p50 = pct(0.50)
    p90 = pct(0.90)
    p95 = pct(0.95)
    p99 = pct(0.99)
    p999 = pct(0.999)
    max_lat = round(latencies[-1], 3) if n > 0 else 0
    min_lat = round(latencies[0], 3) if n > 0 else 0

    ok_count = sum(1 for r in shared_results if r[1] and r[2] == 200)
    err_count = len(failures)

    print(f"\n{'='*44}")
    print("  FAST BENCHMARK — sub-ms accurate")
    print(f"{'='*44}")
    print(f"  total      : {n}")
    print(f"  duration   : {int(elapsed*1000)} ms")
    print(f"  rps        : {rps}")
    print(f"{'-'*44}")
    print("  LATENCY (ms)")
    print(f"    min   : {min_lat}")
    print(f"    avg   : {avg}")
    print(f"    p50   : {p50}")
    print(f"    p90   : {p90}")
    print(f"    p95   : {p95}")
    print(f"    p99   : {p99}")
    print(f"    p99.9 : {p999}")
    print(f"    max   : {max_lat}")
    print(f"{'-'*44}")
    print(f"  ok={ok_count}  err={err_count}  failures={len(failures)}")
    print(f"{'='*44}")

    result = {
        "total": total, "concurrency": concurrency, "duration_ms": round(elapsed*1000, 1),
        "rps": rps, "min_ms": min_lat, "avg_ms": avg,
        "p50": p50, "p90": p90, "p95": p95, "p99": p99, "p99.9": p999, "max_ms": max_lat,
        "ok": ok_count, "err": err_count, "failures": len(failures)
    }
    print(json.dumps(result, indent=2))

if __name__ == "__main__":
    run(5000, 50, 500)
