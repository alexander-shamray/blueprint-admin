namespace Admin.Host.Fakes;

/// <summary>
/// What run-locally.md's "Load run (k6)" blocks print in FakePlatform mode, each recorded from a live run of that block on
/// 2026-10-18, under <c>powershell -NoProfile -Command</c> from the backend clone at <c>fc9b74fa</c>, with k6 v2.2.0 and the stack
/// up (#78). The run's progress lines are cut to its first two ticks and its last two, of 190; its <c>level=info</c> console line,
/// which k6 writes to stderr, is kept on stdout where it appeared. Everything else is the recording. Exact text is not a contract;
/// the shapes are: the markers, the fixed lines and <c>k6 exit: 0</c>.
/// </summary>
internal static class LoadRunRecordings
{
    /// <summary>The k6 read of run-locally.md step 0, which the workstation doctor makes too.</summary>
    public static readonly string K6 =
        """
        k6.exe v2.2.0 (commit/00a9a1b7f5, go1.26.5, windows/amd64)
        """;

    /// <summary>Prometheus answering through Grafana's proxy.</summary>
    public static readonly string Prometheus =
        """
        prometheus ready: yes
        """;

    /// <summary>The client block on a stack that already had <c>slo-run</c>, so it created nothing.</summary>
    public static readonly string SloClient =
        """
        slo-run: already there
        slo-run roles: inventory:admin,orders:write
        """;

    /// <summary>The token check, which passed.</summary>
    public static readonly string TokenCheck =
        """
        permission: inventory:admin,orders:write
        sub: 464ff5a8-8dba-4fc5-807c-13ef2f031dd9
        iss: http://localhost:8080/realms/commerce
        token check: pass
        """;

    /// <summary>The product read: the seeded Oak bookshelf, in EUR.</summary>
    public static readonly string Product =
        """
        --- product ---
        5eed0000-0000-0000-0000-000000000001
        --- currency ---
        EUR
        """;

    /// <summary>The restock, with nothing reserved by an earlier run.</summary>
    public static readonly string Restock =
        """
        product: 5eed0000-0000-0000-0000-000000000001
        available: 100000
        reserved: 0
        """;

    /// <summary>The run, which passed.</summary>
    public static readonly string Run =
        """
                 /\      Grafana   /‾‾/
            /\  /  \     |\  __   /  /
           /  \/    \    | |/ /  /   ‾‾\
          /          \   |   (  |  (‾)  |
         / __________ \  |_|\_\  \_____/


             execution: local
                script: deploy/observability/slo/slo.js
                output: -

             scenarios: (100.00%) 2 scenarios, 60 max VUs, 3m40s max duration (incl. graceful stop):
                      * query: 1.00 iterations/s for 3m0s (maxVUs: 10-30, exec: browseCatalog, gracefulStop: 30s)
                      * command: 3.00 iterations/s for 3m0s (maxVUs: 10-30, exec: placeOrder, startTime: 10s, gracefulStop: 30s)


        running (0m01.0s), 00/20 VUs, 1 complete and 0 interrupted iterations
        query     [   1% ] 00/10 VUs  0m00.9s/3m0s  1.00 iters/s
        command • [   0% ] waiting    09.1s

        running (0m02.0s), 00/20 VUs, 2 complete and 0 interrupted iterations
        query     [   1% ] 00/10 VUs  0m01.9s/3m0s  1.00 iters/s
        command • [   0% ] waiting    08.1s

        running (3m09.0s), 00/20 VUs, 717 complete and 0 interrupted iterations
        query   ✓ [ 100% ] 00/10 VUs  3m0s          1.00 iters/s
        command   [  99% ] 00/10 VUs  2m58.9s/3m0s  3.00 iters/s

        running (3m10.0s), 00/20 VUs, 720 complete and 0 interrupted iterations
        query   ✓ [ 100% ] 00/10 VUs  3m0s          1.00 iters/s
        command   [ 100% ] 00/10 VUs  2m59.9s/3m0s  3.00 iters/s


          █ THRESHOLDS

            checks
            ✓ 'rate==1' rate=100.00%

            http_req_failed
            ✓ 'rate<0.01' rate=0.00%

            slo_command_duration
            ✓ 'p(95)<600' p(95)=42.81ms

            slo_query_duration
            ✓ 'p(95)<400' p(95)=15.24ms


          █ TOTAL RESULTS

            checks_total.......: 1262    6.637721/s
            checks_succeeded...: 100.00% 1262 out of 1262
            checks_failed......: 0.00%   0 out of 1262

            ✓ catalog listing is 200
            ✓ order accepted
            ✓ order not refused by the domain

            CUSTOM
            slo_command_duration...........: avg=24.09ms min=9.53ms med=22.37ms max=80.93ms p(90)=37.9ms  p(95)=42.81ms
            slo_query_duration.............: avg=7.3ms   min=2.96ms med=6.01ms  max=28.64ms p(90)=11.39ms p(95)=15.24ms

            HTTP
            http_req_duration..............: avg=19.7ms  min=2.61ms med=19.05ms max=80.93ms p(90)=35.15ms p(95)=40.15ms
              { expected_response:true }...: avg=19.7ms  min=2.61ms med=19.05ms max=80.93ms p(90)=35.15ms p(95)=40.15ms
            http_req_failed................: 0.00%  0 out of 730
            http_reqs......................: 730    3.839569/s

            EXECUTION
            iteration_duration.............: avg=20.25ms min=2.96ms med=19.47ms max=82.73ms p(90)=35.69ms p(95)=40.61ms
            iterations.....................: 721    3.792232/s
            vus............................: 0      min=0        max=0
            vus_max........................: 20     min=20       max=20

            NETWORK
            data_received..................: 1.0 MB 5.4 kB/s
            data_sent......................: 763 kB 4.0 kB/s




        time="2026-10-18T15:28:24+05:00" level=info msg="SLO run: not evaluated — availability (monthly), messaging.delivery.lag (not exercised by this traffic)." source=console

        running (3m10.1s), 00/20 VUs, 721 complete and 0 interrupted iterations
        query   ✓ [ 100% ] 00/10 VUs  3m0s  1.00 iters/s
        command ✓ [ 100% ] 00/10 VUs  3m0s  3.00 iters/s
        k6 exit: 0
        """;

    /// <summary>
    /// Each <see cref="Telemetry.GoldenSignals"/> query's answer through the live Grafana's Prometheus proxy 150 seconds into the run
    /// above, keyed as <c>grafana-prometheus-golden-signals.json</c> is. No 5xx, 401 or 422 series: the run sent none.
    /// </summary>
    public const string GoldenSignals =
        """
        {
        "RequestRate": {"status":"success","data":{"resultType":"vector","result":[{"metric":{"service_name":"Notifications.Api"},"value":[1792319262.549,"0.19167784787445935"]},{"metric":{"service_name":"Web.Bff"},"value":[1792319262.549,"0.2916848969727274"]},{"metric":{"service_name":"Privacy.Api"},"value":[1792319262.549,"0.2916788199508312"]},{"metric":{"service_name":"Payments.Api"},"value":[1792319262.549,"0.29165937518228713"]},{"metric":{"service_name":"Notifications.Worker"},"value":[1792319262.549,"0.19166906252994828"]},{"metric":{"service_name":"Ordering.Api"},"value":[1792319262.549,"1.7458988045385033"]},{"metric":{"service_name":"Shipping.Worker"},"value":[1792319262.549,"0.19166267369429804"]},{"metric":{"service_name":"Catalog.Api"},"value":[1792319262.549,"0.8167040989378681"]},{"metric":{"service_name":"Inventory.Api"},"value":[1792319262.549,"0.3041869457963864"]},{"metric":{"service_name":"Gateway.Api"},"value":[1792319262.549,"2.287642977686105"]}]}},
        "ErrorRatio": {"status":"success","data":{"resultType":"vector","result":[]}},
        "LatencyP99": {"status":"success","data":{"resultType":"vector","result":[{"metric":{"service_name":"Web.Bff"},"value":[1792319266.674,"0.009220000000000006"]},{"metric":{"service_name":"Privacy.Api"},"value":[1792319266.674,"0.021075000000000045"]},{"metric":{"service_name":"Payments.Api"},"value":[1792319266.674,"0.009430000000000022"]},{"metric":{"service_name":"Catalog.Api"},"value":[1792319266.674,"0.018174999999999997"]},{"metric":{"service_name":"Gateway.Api"},"value":[1792319266.674,"0.04894531249999999"]},{"metric":{"service_name":"Notifications.Api"},"value":[1792319266.674,"0.004999009900990099"]},{"metric":{"service_name":"Notifications.Worker"},"value":[1792319266.674,"0.009925000000000007"]},{"metric":{"service_name":"Ordering.Api"},"value":[1792319266.674,"0.04893750000000001"]},{"metric":{"service_name":"Shipping.Worker"},"value":[1792319266.674,"0.009150000000000004"]},{"metric":{"service_name":"Inventory.Api"},"value":[1792319266.674,"0.023714285714285743"]}]}},
        "DomainRefusalRate": {"status":"success","data":{"resultType":"vector","result":[]}},
        "UnauthorisedRate": {"status":"success","data":{"resultType":"vector","result":[]}},
        "CommandP95": {"status":"success","data":{"resultType":"vector","result":[{"metric":{"service_name":"Inventory.Api"},"value":[1792319272.817,"0.03845238095238095"]},{"metric":{"service_name":"Shipping.Worker"},"value":[1792319272.817,"0.03585937500000002"]},{"metric":{"service_name":"Payments.Api"},"value":[1792319272.817,"0.0795561224489796"]},{"metric":{"service_name":"Notifications.Worker"},"value":[1792319272.817,"0.04200231481481481"]},{"metric":{"service_name":"Ordering.Api"},"value":[1792319272.817,"0.039865702479338835"]}]}},
        "QueryP95": {"status":"success","data":{"resultType":"vector","result":[{"metric":{"service_name":"Ordering.Api"},"value":[1792319274.882,"0.0049875"]},{"metric":{"service_name":"Catalog.Api"},"value":[1792319274.882,"0.0048658536585365845"]},{"metric":{"service_name":"Inventory.Api"},"value":[1792319274.882,"0.00475"]}]}}
        }
        """;
}
