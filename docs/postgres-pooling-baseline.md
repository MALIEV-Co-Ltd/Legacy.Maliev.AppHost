# Local PostgreSQL pooling baseline

This evidence is intentionally local and disposable. It does not validate CloudNativePG failover,
persistent volumes, pod scheduling, or production traffic, and it does not authorize a GKE deploy.

## Reproduce

From the repository root with Docker Desktop running:

```powershell
.\scripts\benchmark-postgres-pooling.ps1
```

The 2026-07-18 baseline used PostgreSQL `18-alpine`, PgBouncer `1.25.2-p0`, pgbench scale 10,
eight clients, four jobs, ten seconds per case, and three rounds with rotated case ordering. The
PostgreSQL container was limited to 0.75 CPU and 1024 MiB. Each pooler was limited to 0.10 CPU and
96 MiB and used the dormant GitOps contract: `default_pool_size=3`, `reserve_pool_size=1`,
`max_client_conn=200`, `min_pool_size=0`, and `server_idle_timeout=60`.

| Target | Median latency | Median transactions/s | Maximum PostgreSQL backends |
| --- | ---: | ---: | ---: |
| Direct | 18.826 ms | 424.954 | 8 |
| Session pool | 45.284 ms | 176.662 | 4 |
| Transaction pool | 48.189 ms | 166.014 | 4 |

Both pool modes reduced peak PostgreSQL backends by 50% for this eight-client workload. This local
resource-constrained comparison is a capacity baseline, not a production throughput forecast. The
poolers deliberately receive much less CPU than PostgreSQL, so their latency and throughput numbers
must not be interpreted as provider-neutral overhead.

The transaction compatibility probes passed for a multi-statement transaction, `SET LOCAL`, and a
transaction-scoped advisory lock. The transaction pooler recovered a successful query 3.120 seconds
after its container restart. The separate full-stack verifier already proves retained EF Core
application flows through the transaction pool while migrations and bootstrap operations remain on
the direct connection.

## Saturation evidence

At 20 clients, four jobs, ten seconds, and the same three-server plus one-reserve pool, transaction
mode did not complete reliably. Clients queued at `BEGIN` and eventually reached PgBouncer's default
120-second `query_wait_timeout`. Failure diagnostics showed four healthy idle session-pool backends
and three healthy idle transaction-pool backends after the aborted clients disconnected; the failure
was pool admission starvation under this 100m CPU / small-pool load, not a PostgreSQL outage.

The checked-in default therefore uses eight clients, which is still 2.7 times the configured primary
backend pool size and completes repeatably. The harness remains fail closed for command failures and
emits PostgreSQL plus pool state diagnostics on an unsuccessful case. Increasing concurrency or
pool sizes requires a new measured baseline and must remain within the existing cluster's resource
budget.

## Remaining deployment gates

- Reclaim enough capacity in the existing GKE node pool for the prepared two-instance legacy
  PostgreSQL topology; no additional node pool or paid database service is allowed.
- Validate CloudNativePG primary switchover/failover, pooler reconnection, recovery time, and data
  consistency in an owner-approved non-production canary.
- Re-run representative service traffic and confirm database backend counts, latency, error rate,
  and rollback behavior before production cutover approval.
