using Admin.Host.Api;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// What this console copies about the product-image store and the edge in front of it (ADR-106, §10.8), each fact read where the
/// backend states it: the probes' routes, the edge's rules, the BFF's two endpoints, the keys and types, and the errors Catalog
/// answers with, which the fakes repeat.
/// </summary>
public sealed class ObjectStoreDriftTests
{
    private static readonly string[] Infrastructure = ["deploy", "compose", "infrastructure.yml"];

    private static readonly string[] EdgeConf = ["deploy", "compose", "object-store", "edge.conf"];

    private static readonly string[] ImageKeys = ["src", "BuildingBlocks", "Common.Application", "ProductImageKeys.cs"];

    [Fact]
    public void The_store_is_probed_where_its_own_healthcheck_asks()
    {
        Backend.Read(Infrastructure).ShouldContain($"wget -q -O /dev/null {new AdminOptions().ObjectStoreUrl}/status");
    }

    [Fact]
    public void The_edge_is_probed_at_the_route_it_answers_for_its_healthcheck()
    {
        Backend.Read(EdgeConf).ShouldContain("location = /healthz");
        Backend.Read(Infrastructure).ShouldContain("http://127.0.0.1:8080/healthz");
    }

    [Fact]
    public void The_edge_serves_published_images_to_a_read_alone_from_the_bucket_the_fake_store_holds()
    {
        string edge = Backend.Read(EdgeConf);

        edge.ShouldContain("location /products/");
        edge.ShouldContain("limit_except GET HEAD");
        edge.ShouldContain($"proxy_pass http://object-store:8333/{FakeObjectStore.Bucket}/products/;");
        edge.ShouldContain("add_header X-Content-Type-Options \"nosniff\" always;");
    }

    [Fact]
    public void The_edge_operation_reads_the_prefix_the_edge_serves_and_the_key_shape_the_backend_mints()
    {
        ApiOperation read = CuratedOperations.All(new AdminOptions()).Single(o => o.Id == "edge:GetProductImage");
        string keys = Backend.Read(ImageKeys);

        read.Url.ShouldBe($"{new AdminOptions().ImageEdgeUrl}/products/{{seller}}/{{file}}");
        keys.ShouldContain("public const string ProductPrefix = \"products/\";");
        keys.ShouldContain("public const string UploadPrefix = \"uploads/\";");
        keys.ShouldContain("$\"{ProductPrefix}{seller:D}/{image:D}.{extension}\"");
    }

    [Fact]
    public void The_types_and_size_the_fake_signs_are_the_backends()
    {
        string keys = Backend.Read(ImageKeys);

        keys.ShouldContain("public const long MaxSizeBytes = 5 * 1024 * 1024;");
        keys.ShouldContain("[\"image/jpeg\"] = \"jpg\"");
        keys.ShouldContain("[\"image/png\"] = \"png\"");
        keys.ShouldContain("[\"image/webp\"] = \"webp\"");
        keys.ShouldContain("$\"{UploadPrefix}{seller:D}/{image:D}.{extension}\"");
        SampleImage.ContentType.ShouldBe("image/png");
    }

    [Fact]
    public void The_bff_serves_the_upload_and_the_saved_address_where_the_curated_operations_send()
    {
        string upload = Backend.Read("src", "BFF", "Web.Bff", "ProductImages", "ProductImageEndpoints.cs");
        string address = Backend.Read("src", "BFF", "Web.Bff", "Endpoints", "SavedAddressEndpoints.cs");

        upload.ShouldContain(".MapGroup(\"/v1/product-images\")");
        upload.ShouldContain(".RequireAuthorization(BffPermissions.CatalogWrite)");
        upload.ShouldContain(".MapPost(");
        address.ShouldContain(".MapGroup(\"/v1/saved-address\")");
        address.ShouldContain(".MapGet(");
        address.ShouldContain(".MapPut(");
        Backend.Read("src", "BFF", "Web.Bff", "ProductImages", "BffPermissions.cs").ShouldContain("\"catalog:write\"");
    }

    [Fact]
    public void The_upload_answers_with_the_four_fields_the_scenario_reads()
    {
        string issued = Backend.Read("src", "BFF", "Web.Bff", "ProductImages", "ProductImageUploads.cs");

        issued.ShouldContain("public sealed record ProductImageUpload(");
        issued.ShouldContain("Uri UploadUrl,");
        issued.ShouldContain("IReadOnlyDictionary<string, string> Headers,");
        issued.ShouldContain("string Key,");
        issued.ShouldContain("DateTimeOffset ExpiresAt);");
    }

    [Fact]
    public void Catalog_sniffs_what_it_attaches_and_answers_its_rule_errors_as_the_fake_does()
    {
        string errors = Backend.Read("src", "Services", "Catalog", "Catalog.Application", "Products", "ProductErrors.cs");

        errors.ShouldContain("\"product.image_key_invalid\"");
        errors.ShouldContain("\"product.image_not_uploaded\"");
        errors.ShouldContain("\"product.image_not_an_image\"");
        Backend.Read("src", "BuildingBlocks", "Common.Web", "ResultExtensions.cs")
            .ShouldContain("ErrorType.Rule => StatusCodes.Status422UnprocessableEntity");
        Backend.Read("src", "Services", "Catalog", "Catalog.Application", "Products", "PublishProduct", "PublishProductHandler.cs")
            .ShouldContain("ImageAttachOutcome.NotAnImage");
    }

    [Fact]
    public void A_product_read_is_the_anonymous_get_by_guid_the_fake_serves()
    {
        string endpoints = Backend.Read("src", "Services", "Catalog", "Catalog.Api", "Endpoints", "ProductEndpoints.cs");

        endpoints.ShouldContain("\"/{id:guid}\"");
        endpoints.ShouldContain(".WithName(\"GetProduct\")");
    }
}
