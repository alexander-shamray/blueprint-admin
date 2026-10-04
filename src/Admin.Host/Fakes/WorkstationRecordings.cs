namespace Admin.Host.Fakes;

/// <summary>
/// What this workstation answered to the workstation doctor's reads on 2026-10-04, with the stack up, for
/// <see cref="FakePlatformScripts"/> to replay. Kept here rather than under <c>fixtures</c>, whose files are
/// embedded by a project file this change does not edit.
/// </summary>
internal static class WorkstationRecordings
{
    /// <summary>
    /// <c>docker compose config --format json</c>, cut to what the doctor reads: each service's published ports and
    /// the gateway's CORS entries. The rest of the model, connection strings with the local passwords among it, is
    /// left out, so no credential reaches a recording.
    /// </summary>
    public const string ComposeConfig = """
        {
          "name": "commerce",
          "services": {
            "carrier-simulator": {
              "ports": [
                {
                  "target": 80,
                  "published": "5191",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "catalog-api": {
              "ports": [
                {
                  "target": 8080,
                  "published": "5102",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "catalog-migrator": {},
            "gateway": {
              "ports": [
                {
                  "target": 8080,
                  "published": "5000",
                  "host_ip": "127.0.0.1"
                }
              ],
              "environment": {
                "Cors__Enabled": "true",
                "Cors__Origins__0": "http://localhost:5173",
                "Cors__Origins__1": "https://localhost"
              }
            },
            "grafana": {
              "ports": [
                {
                  "target": 3000,
                  "published": "3000",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "inventory-api": {
              "ports": [
                {
                  "target": 8080,
                  "published": "5103",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "inventory-migrator": {},
            "keycloak": {
              "ports": [
                {
                  "target": 8080,
                  "published": "8080",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "mailpit": {
              "ports": [
                {
                  "target": 1025,
                  "published": "1025",
                  "host_ip": "127.0.0.1"
                },
                {
                  "target": 8025,
                  "published": "8025",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "notifications-migrator": {},
            "notifications-worker": {},
            "ordering-api": {
              "ports": [
                {
                  "target": 8080,
                  "published": "5101",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "ordering-migrator": {},
            "otel-collector": {
              "ports": [
                {
                  "target": 4317,
                  "published": "4317",
                  "host_ip": "127.0.0.1"
                },
                {
                  "target": 4318,
                  "published": "4318",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "payments-api": {
              "ports": [
                {
                  "target": 8080,
                  "published": "5104",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "payments-migrator": {},
            "psp-simulator": {
              "ports": [
                {
                  "target": 80,
                  "published": "5190",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "rabbitmq": {
              "ports": [
                {
                  "target": 5672,
                  "published": "5672",
                  "host_ip": "127.0.0.1"
                },
                {
                  "target": 15672,
                  "published": "15672",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "redis-cache": {
              "ports": [
                {
                  "target": 6379,
                  "published": "6379",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "redis-coordination": {
              "ports": [
                {
                  "target": 6379,
                  "published": "6380",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "shipping-migrator": {},
            "shipping-worker": {},
            "sql": {
              "ports": [
                {
                  "target": 1433,
                  "published": "1433",
                  "host_ip": "127.0.0.1"
                }
              ]
            },
            "web-bff": {
              "ports": [
                {
                  "target": 8080,
                  "published": "5200",
                  "host_ip": "127.0.0.1"
                }
              ]
            }
          }
        }
        """;

    /// <summary><c>docker compose images --format json</c>, whole; every built image predates the clone's last commit.</summary>
    public const string ComposeImages = """
        [{"ID":"sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89","ContainerName":"commerce-sql-1","Repository":"mcr.microsoft.com/mssql/server","Tag":"2022-latest","Platform":"linux/amd64","Size":624874967,"Created":"2026-07-08T06:44:23.764648349Z","LastTagTime":"2026-08-08T04:44:13.705718467Z"},{"ID":"sha256:72044ff4eb4c7d32f4e68444f9c531c01ae52ac723226c9ab5f3f2206ed07e29","ContainerName":"commerce-payments-api-1","Repository":"commerce-payments-api","Tag":"latest","Platform":"linux/amd64","Size":82124699,"Created":"2026-10-03T07:18:28.657141039Z","LastTagTime":"2026-10-03T13:54:15.343928833Z"},{"ID":"sha256:48728047445ba86e3fe593e1ce3befcdac7a6a7dd3aaa01f98a10187d9d440d9","ContainerName":"commerce-notifications-worker-1","Repository":"commerce-notifications-worker","Tag":"latest","Platform":"linux/amd64","Size":85288542,"Created":"2026-10-03T13:54:27.249496072Z","LastTagTime":"2026-10-03T13:54:29.241238367Z"},{"ID":"sha256:bb182ac3174a20923fa58f00d1790fa71eb06cc0150078c0b0e8feb9e6611492","ContainerName":"commerce-grafana-1","Repository":"grafana/otel-lgtm","Tag":"latest","Platform":"linux/amd64","Size":809906807,"Created":"2026-08-07T07:08:18Z","LastTagTime":"2026-08-08T20:57:07.578152174Z"},{"ID":"sha256:2a951c2057d556d6a2b7af81b7e75c2fb52d0390dcd3f34e3dfb880be5660cd5","ContainerName":"commerce-inventory-api-1","Repository":"commerce-inventory-api","Tag":"latest","Platform":"linux/amd64","Size":81708039,"Created":"2026-10-03T07:16:43.524878063Z","LastTagTime":"2026-10-03T13:54:15.326366716Z"},{"ID":"sha256:5a9025fc590b96c7d464a13d7e6750c3486f0c11f8355383c612a830ce224c6a","ContainerName":"commerce-shipping-migrator-1","Repository":"commerce-shipping-migrator","Tag":"latest","Platform":"linux/amd64","Size":71956403,"Created":"2026-10-03T07:18:25.856887557Z","LastTagTime":"2026-10-03T13:54:13.069153264Z"},{"ID":"sha256:09a381c715ab0b111835b70f2905955274843a219c6f27efb348e4d9f4086858","ContainerName":"commerce-keycloak-1","Repository":"quay.io/keycloak/keycloak","Tag":"26.0","Platform":"linux/amd64","Size":237761605,"Created":"2025-02-20T14:47:15.267919298Z","LastTagTime":"2026-08-08T20:49:47.923290505Z"},{"ID":"sha256:132f17130dc15228366fd899a6c7dc3e4f75966e70684fb6d270028d817b0589","ContainerName":"commerce-shipping-worker-1","Repository":"commerce-shipping-worker","Tag":"latest","Platform":"linux/amd64","Size":82261225,"Created":"2026-10-03T07:18:50.604136911Z","LastTagTime":"2026-10-03T13:54:15.275841142Z"},{"ID":"sha256:1a2f6a30bf4522a31735b1a6d00937c763f0e1a73801565d72a439b1e4f5e664","ContainerName":"commerce-ordering-api-1","Repository":"commerce-ordering-api","Tag":"latest","Platform":"linux/amd64","Size":82340699,"Created":"2026-10-03T07:19:30.597080431Z","LastTagTime":"2026-10-03T13:54:15.335076024Z"},{"ID":"sha256:c5918f78992ee73b0d6f0e599423ac5ec52dd5d9726733114d6eca53d5a32ed5","ContainerName":"commerce-otel-collector-1","Repository":"otel/opentelemetry-collector-contrib","Tag":"latest","Platform":"linux/amd64","Size":107993157,"Created":"2026-08-04T19:25:18.411024384Z","LastTagTime":"2026-08-08T17:20:06.720307395Z"},{"ID":"sha256:ed9b00c609e77e99c79b93f1178255ebc271868920f2c69a8d166bd5634ed10d","ContainerName":"commerce-mailpit-1","Repository":"axllent/mailpit","Tag":"v1.31.3","Platform":"linux/amd64","Size":14275701,"Created":"2026-09-27T07:05:40.768116049Z","LastTagTime":"2026-10-03T02:15:43.639144343Z"},{"ID":"sha256:5af99c8f84c8d757a2a12a2d67503423c7763c2dc29885df7e4bd0d1ce8afcfe","ContainerName":"commerce-rabbitmq-1","Repository":"commerce-rabbitmq","Tag":"latest","Platform":"linux/amd64","Size":87594639,"Created":"2026-10-02T17:19:29.239926431Z","LastTagTime":"2026-10-03T13:54:13.059353493Z"},{"ID":"sha256:ac843429a8051eaa1943234ac9b1231d9f689160cc360ebaf46f3a9a7330c87d","ContainerName":"commerce-carrier-simulator-1","Repository":"sheyenrath/wiremock.net","Tag":"2.12.0","Platform":"linux/amd64","Size":101779412,"Created":"2026-07-09T17:42:44.665220486Z","LastTagTime":"2026-09-19T13:56:23.929291333Z"},{"ID":"sha256:d92a988c381ef7fe5a5419f48b13b008f42f7ca9bb31e0c6b2d3731b2cd851ac","ContainerName":"commerce-ordering-migrator-1","Repository":"commerce-ordering-migrator","Tag":"latest","Platform":"linux/amd64","Size":71233880,"Created":"2026-10-03T07:20:37.08727626Z","LastTagTime":"2026-10-03T13:54:13.062148282Z"},{"ID":"sha256:29d531d6d38c8dba4cf172c744c9a022dc42aae1868c81ab40fbfc6e79f306fb","ContainerName":"commerce-notifications-migrator-1","Repository":"commerce-notifications-migrator","Tag":"latest","Platform":"linux/amd64","Size":74979336,"Created":"2026-10-03T13:54:25.542011058Z","LastTagTime":"2026-10-03T13:54:27.814795468Z"},{"ID":"sha256:c6fb4fa1168da263afe5ed52d37fbbe2ec0da3f78b3736f7d66378045cf9e265","ContainerName":"commerce-inventory-migrator-1","Repository":"commerce-inventory-migrator","Tag":"latest","Platform":"linux/amd64","Size":71090547,"Created":"2026-10-03T07:17:18.199674566Z","LastTagTime":"2026-10-03T13:54:13.073336696Z"},{"ID":"sha256:5c4cdf7a8c7e90865723061315a2091f0da9c040c2343437bc1d1b7309163a37","ContainerName":"commerce-catalog-api-1","Repository":"commerce-catalog-api","Tag":"latest","Platform":"linux/amd64","Size":82170966,"Created":"2026-10-03T07:19:05.030737012Z","LastTagTime":"2026-10-03T13:54:15.32340467Z"},{"ID":"sha256:2473275dfba0dfe2a755992a8a7e6ec472dcad00511bf12ad16992bd1701f27f","ContainerName":"commerce-web-bff-1","Repository":"commerce-web-bff","Tag":"latest","Platform":"linux/amd64","Size":76635795,"Created":"2026-10-03T07:19:20.697258547Z","LastTagTime":"2026-10-03T13:54:15.249795642Z"},{"ID":"sha256:d386a820a8f416c443abf58da9a57f98eb72a0caee3ed65483f74fbe0f69f05c","ContainerName":"commerce-gateway-1","Repository":"commerce-gateway","Tag":"latest","Platform":"linux/amd64","Size":72906069,"Created":"2026-10-03T07:15:15.40791944Z","LastTagTime":"2026-10-03T13:54:14.778961922Z"},{"ID":"sha256:ed09736773dd209c4823f194b77c1bebf05c8b2179d9063073b6e98786f68186","ContainerName":"commerce-catalog-migrator-1","Repository":"commerce-catalog-migrator","Tag":"latest","Platform":"linux/amd64","Size":71058292,"Created":"2026-10-03T07:17:44.404576359Z","LastTagTime":"2026-10-03T13:54:13.057576763Z"},{"ID":"sha256:05933b8511c8659f3a99d2440308aa2e1e839c4a7ec1bdeba7ceb893535027e2","ContainerName":"commerce-payments-migrator-1","Repository":"commerce-payments-migrator","Tag":"latest","Platform":"linux/amd64","Size":71512109,"Created":"2026-10-03T07:19:20.119571994Z","LastTagTime":"2026-10-03T13:54:13.060812919Z"},{"ID":"sha256:ac843429a8051eaa1943234ac9b1231d9f689160cc360ebaf46f3a9a7330c87d","ContainerName":"commerce-psp-simulator-1","Repository":"sheyenrath/wiremock.net","Tag":"2.12.0","Platform":"linux/amd64","Size":101779412,"Created":"2026-07-09T17:42:44.665220486Z","LastTagTime":"2026-09-19T13:56:23.929291333Z"},{"ID":"sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2","ContainerName":"commerce-redis-coordination-1","Repository":"redis","Tag":"7-alpine","Platform":"linux/amd64","Size":16274952,"Created":"2026-07-26T04:42:13.95278683Z","LastTagTime":"2026-08-08T17:10:03.626749345Z"},{"ID":"sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2","ContainerName":"commerce-redis-cache-1","Repository":"redis","Tag":"7-alpine","Platform":"linux/amd64","Size":16274952,"Created":"2026-07-26T04:42:13.95278683Z","LastTagTime":"2026-08-08T17:10:03.626749345Z"}]
        """;

    /// <summary>
    /// The listener line, cut to the published ports, every one of them Docker's while the stack ran, except 5672 and
    /// 15672, which carry the host RabbitMQ (erl, pid 7376) that held them on 2026-10-03 before it was stopped: the
    /// panel's red row, recorded from the state that motivated it.
    /// </summary>
    public const string Listeners = """
        [
         {
          "LocalPort": 1025,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 1433,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 3000,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 4317,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 4318,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5000,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5101,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5102,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5103,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5104,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5190,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5191,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5200,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 5672,
          "OwningProcess": 7376,
          "Process": "erl"
         },
         {
          "LocalPort": 6379,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 6380,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 8025,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 8080,
          "OwningProcess": 36668,
          "Process": "com.docker.backend"
         },
         {
          "LocalPort": 15672,
          "OwningProcess": 7376,
          "Process": "erl"
         }
        ]
        """;
}
