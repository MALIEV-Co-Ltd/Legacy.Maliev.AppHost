using DiagnosticsProcess = System.Diagnostics.Process;
using DiagnosticsProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using Legacy.Maliev.AuthService.Infrastructure;
using Legacy.Maliev.AppHost.Topology;
using Legacy.Maliev.CountryService.Data;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Domain;
using Legacy.Maliev.EmployeeService.Data;
using Legacy.Maliev.CatalogService.Data;
using Legacy.Maliev.ProcurementService.Data;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Legacy.Maliev.CareerService.Data;
using Legacy.Maliev.CareerService.Domain;
using Legacy.Maliev.ContactService.Data;
using Legacy.Maliev.AccountingService.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var workload = args.FirstOrDefault()
    ?? throw new InvalidOperationException("A migration workload is required.");
var snapshotDirectory = Environment.GetEnvironmentVariable("LEGACY_SNAPSHOT_DIRECTORY");

if (string.Equals(Environment.GetEnvironmentVariable("LEGACY_SKIP_MIGRATE"), "true", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(snapshotDirectory))
    {
        // GKE validation mode: the target database already has the real migrated schema and
        // data. Never touch it here — beyond MigrateAsync, several workloads below also seed
        // rows (SeedQuotationAsync, SeedOrderAsync, etc.), which must not run against real data.
        Console.WriteLine("LEGACY_SKIP_MIGRATE=true; skipping migration and seeding.");
        return;
    }

    var snapshotDatabase = workload == "snapshot"
        ? args.Skip(1).SingleOrDefault()
        : DatabaseNameForWorkload(workload);
    if (string.IsNullOrWhiteSpace(snapshotDatabase))
    {
        throw new InvalidOperationException("A snapshot workload requires a database name.");
    }

    var snapshotConnectionName = workload == "snapshot"
        ? "SnapshotDb"
        : ConnectionNameForWorkload(workload);
    var snapshotConnectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{snapshotConnectionName}");
    if (string.IsNullOrWhiteSpace(snapshotConnectionString))
    {
        throw new InvalidOperationException($"The {snapshotConnectionName} snapshot connection string is required.");
    }

    await RestoreSnapshotAsync(snapshotDirectory, snapshotDatabase, snapshotConnectionString);
    return;
}

if (workload == "snapshot")
{
    throw new InvalidOperationException("Snapshot workloads require LEGACY_SKIP_MIGRATE=true.");
}

var connectionName = ConnectionNameForWorkload(workload);
var connectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{connectionName}");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException($"The {connectionName} connection string is required.");
}

await MigrateAsync(workload, connectionString);

static string ConnectionNameForWorkload(string workload) => workload switch
{
    "auth" => "RefreshSessions",
    "customer-identity" => "CustomerIdentity",
    "employee-identity" => "EmployeeIdentity",
    "country" => "CountryDbContext",
    "customer" => "CustomerDbContext",
    "employee" => "EmployeeDbContext",
    "catalog" => "CatalogDbContext",
    "supplier" => "SupplierDbContext",
    "purchase-order" => "PurchaseOrderDbContext",
    "file" => "FileDbContext",
    "order" => "OrderDbContext",
    "order-status" => "OrderStatusDbContext",
    "quotation" => "QuotationDbContext",
    "quotation-request" => "QuotationRequestDbContext",
    "career" => "CareerDbContext",
    "contact" => "ContactRequestDbContext",
    "payment" => "PaymentDbContext",
    "invoice" => "InvoiceDbContext",
    "receipt" => "ReceiptDbContext",
    _ => throw new InvalidOperationException($"Unknown migration workload '{workload}'."),
};

static string DatabaseNameForWorkload(string workload) => workload switch
{
    "country" => "Country",
    "customer-identity" => "CustomerIdentity",
    "employee-identity" => "EmployeeIdentity",
    "customer" => "Customer",
    "employee" => "Employee",
    "catalog" => "Material",
    "supplier" => "Supplier",
    "purchase-order" => "PurchaseOrder",
    "file" => "Upload",
    "order" => "Order",
    "order-status" => "OrderStatus",
    "quotation" => "Quotation",
    "quotation-request" => "QuotationRequest",
    "career" => "JobOffers",
    "contact" => "Message",
    "payment" => "Payment",
    "invoice" => "Invoice",
    "receipt" => "Receipt",
    _ => throw new InvalidOperationException($"Workload '{workload}' does not map to a migrated database."),
};

static async Task RestoreSnapshotAsync(
    string snapshotDirectory,
    string databaseName,
    string connectionString)
{
    var archivePath = LegacyLocalSnapshot.Load(snapshotDirectory).GetArchivePath(databaseName);
    var connection = new NpgsqlConnectionStringBuilder(connectionString);
    var password = connection.Password;
    var host = connection.Host ?? throw new InvalidOperationException("Snapshot connection host is required.");
    var username = connection.Username ?? throw new InvalidOperationException("Snapshot connection username is required.");
    var database = connection.Database ?? throw new InvalidOperationException("Snapshot connection database is required.");

    var startInfo = new DiagnosticsProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("PG_RESTORE_PATH") ?? "pg_restore",
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("--exit-on-error");
    startInfo.ArgumentList.Add("--clean");
    startInfo.ArgumentList.Add("--if-exists");
    startInfo.ArgumentList.Add("--no-owner");
    startInfo.ArgumentList.Add("--no-privileges");
    startInfo.ArgumentList.Add("--single-transaction");
    startInfo.ArgumentList.Add("--no-password");
    startInfo.ArgumentList.Add("--host");
    startInfo.ArgumentList.Add(host);
    startInfo.ArgumentList.Add("--port");
    startInfo.ArgumentList.Add(connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    startInfo.ArgumentList.Add("--username");
    startInfo.ArgumentList.Add(username);
    startInfo.ArgumentList.Add("--dbname");
    startInfo.ArgumentList.Add(database);
    startInfo.ArgumentList.Add(archivePath);
    if (!string.IsNullOrWhiteSpace(password))
    {
        startInfo.Environment["PGPASSWORD"] = password;
    }

    using var process = new DiagnosticsProcess { StartInfo = startInfo };
    try
    {
        if (!process.Start())
        {
            throw new InvalidOperationException("pg_restore could not be started.");
        }
    }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        throw new InvalidOperationException(
            "The local snapshot requires pg_restore in PATH or PG_RESTORE_PATH.",
            exception);
    }

    var standardOutputTask = process.StandardOutput.ReadToEndAsync();
    var standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await Task.WhenAll(standardOutputTask, standardErrorTask);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"pg_restore failed for database '{databaseName}'.");
    }

    Console.WriteLine($"Restored the verified local PostgreSQL snapshot for database '{databaseName}'.");
}

static async Task MigrateAsync(string workload, string connectionString)
{
    switch (workload)
    {
        case "auth":
            var authOptions = new DbContextOptionsBuilder<RefreshSessionDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new RefreshSessionDbContext(authOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "country":
            var countryOptions = new DbContextOptionsBuilder<CountryDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new CountryDbContext(countryOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "customer-identity":
            var customerIdentityOptions = new DbContextOptionsBuilder<CustomerIdentityDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new CustomerIdentityDbContext(customerIdentityOptions))
            {
                await context.Database.MigrateAsync();
                await SeedIdentityAsync(
                    context,
                    "local-customer",
                    LegacyTopology.LocalCustomerEmail,
                    databaseId: 1,
                    includeCustomerFields: true);
            }

            break;
        case "employee-identity":
            var employeeIdentityOptions = new DbContextOptionsBuilder<EmployeeIdentityDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new EmployeeIdentityDbContext(employeeIdentityOptions))
            {
                await context.Database.MigrateAsync();
                await SeedIdentityAsync(
                    context,
                    "local-employee",
                    LegacyTopology.LocalEmployeeEmail,
                    databaseId: 2,
                    includeCustomerFields: false);
            }

            break;
        case "customer":
            var customerOptions = new DbContextOptionsBuilder<CustomerDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new CustomerDbContext(customerOptions))
            {
                await context.Database.MigrateAsync();
                await SeedCustomerAsync(context);
            }

            break;
        case "employee":
            var employeeOptions = new DbContextOptionsBuilder<EmployeeDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new EmployeeDbContext(employeeOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "catalog":
            var catalogOptions = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new CatalogDbContext(catalogOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "supplier":
            var supplierOptions = new DbContextOptionsBuilder<SupplierDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new SupplierDbContext(supplierOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "purchase-order":
            var purchaseOrderOptions = new DbContextOptionsBuilder<PurchaseOrderDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new PurchaseOrderDbContext(purchaseOrderOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "file":
            var fileOptions = new DbContextOptionsBuilder<FileDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new FileDbContext(fileOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "order":
            var orderOptions = new DbContextOptionsBuilder<OrderDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new OrderDbContext(orderOptions))
            {
                await context.Database.MigrateAsync();
                await SeedOrderAsync(context);
            }

            break;
        case "order-status":
            var orderStatusOptions = new DbContextOptionsBuilder<OrderStatusDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new OrderStatusDbContext(orderStatusOptions))
            {
                await context.Database.MigrateAsync();
                await SeedOrderStatusesAsync(context);
            }

            break;
        case "quotation":
            var quotationOptions = new DbContextOptionsBuilder<QuotationDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new QuotationDbContext(quotationOptions))
            {
                await context.Database.MigrateAsync();
                await SeedQuotationAsync(context);
            }

            break;
        case "quotation-request":
            var quotationRequestOptions = new DbContextOptionsBuilder<QuotationRequestDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new QuotationRequestDbContext(quotationRequestOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "career":
            var careerOptions = new DbContextOptionsBuilder<CareerDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new CareerDbContext(careerOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "contact":
            var contactOptions = new DbContextOptionsBuilder<ContactRequestDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new ContactRequestDbContext(contactOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "payment":
            var paymentOptions = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new PaymentDbContext(paymentOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "invoice":
            var invoiceOptions = new DbContextOptionsBuilder<InvoiceDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new InvoiceDbContext(invoiceOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "receipt":
            var receiptOptions = new DbContextOptionsBuilder<ReceiptDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new ReceiptDbContext(receiptOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
    }
}

static async Task SeedQuotationAsync(QuotationDbContext context)
{
    var quotation = await context.Quotations.SingleOrDefaultAsync(row =>
        row.CustomerId == 1 && row.Comment == "Local CNC quotation");
    if (quotation is null)
    {
        // The legacy quotation schema intentionally retains timestamp without time zone.
        // Npgsql rejects UTC DateTime values for that column type, so keep the seeded
        // wall-clock value explicitly unspecified at this compatibility boundary.
        var timestamp = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Unspecified);
        quotation = new Quotation
        {
            CustomerId = 1,
            Period = 30,
            ExpirationDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Unspecified),
            Subtotal = 100,
            Vat = 7,
            Total = 107,
            WithholdingTax = 3,
            CurrencyId = 764,
            Comment = "Local CNC quotation",
            Fob = "MALIEV",
            ShippedVia = "Courier",
            Terms = "30 days",
            CreatedDate = timestamp,
            ModifiedDate = timestamp,
        };
        context.Quotations.Add(quotation);
        await context.SaveChangesAsync();
    }

    if (quotation.Id != 1)
    {
        throw new InvalidOperationException(
            $"The local quotation ID {quotation.Id} does not match the verifier quotation ID 1.");
    }

    if (!await context.OrderItems.AnyAsync(row => row.QuotationId == quotation.Id))
    {
        context.OrderItems.Add(new QuotationOrderItem
        {
            QuotationId = quotation.Id,
            OrderId = 1,
            Description = "Local CNC quotation line",
            Quantity = 2,
            UnitPrice = 50,
        });
    }

    if (!await context.OrderLinks.AnyAsync(row => row.QuotationId == quotation.Id))
    {
        context.OrderLinks.Add(new QuotationOrderLink
        {
            QuotationId = quotation.Id,
            OrderId = 1,
        });
    }

    if (!await context.Files.AnyAsync(row => row.QuotationId == quotation.Id))
    {
        context.Files.Add(new QuotationFile
        {
            QuotationId = quotation.Id,
            Bucket = "legacy-local-quotations",
            ObjectName = "quotations/local-cnc-quotation.pdf",
        });
    }

    await context.SaveChangesAsync();
}

static async Task SeedOrderAsync(OrderDbContext context)
{
    var category = await context.Categories.SingleOrDefaultAsync(row => row.Name == "Machining");
    if (category is null)
    {
        category = new Category { Name = "Machining" };
        context.Categories.Add(category);
        await context.SaveChangesAsync();
    }

    var process = await context.Processes.SingleOrDefaultAsync(row => row.Name == "CNC");
    if (process is null)
    {
        process = new Process { CategoryId = category.Id, Name = "CNC" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
    }

    var order = await context.Orders.SingleOrDefaultAsync(row =>
        row.CustomerId == 1 && row.Name == "Local CNC order");
    if (order is null)
    {
        order = new Order
        {
            CustomerId = 1,
            Name = "Local CNC order",
            Description = "Aspire customer-order verification",
            ProcessId = process.Id,
            Quantity = 2,
            Manufactured = 0,
            UnitPrice = 100,
            DiscountPercent = 0,
            LeadTime = 5,
            AllowCancellation = true,
            AllowPayment = false,
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();
    }

    if (order.Id != 1)
    {
        throw new InvalidOperationException(
            $"The local order ID {order.Id} does not match the verifier order ID 1.");
    }

    if (!await context.Files.AnyAsync(row => row.OrderId == order.Id))
    {
        context.Files.Add(new OrderFile
        {
            OrderId = order.Id,
            Bucket = "legacy-local-orders",
            ObjectName = "orders/local-cnc-part.step",
        });
        await context.SaveChangesAsync();
    }
}

static async Task SeedOrderStatusesAsync(OrderStatusDbContext context)
{
    var reviewing = await EnsureStatusAsync(context, "Reviewing");
    var cancelled = await EnsureStatusAsync(context, "Cancelled");
    if (!await context.Transitions.AnyAsync(row =>
            row.OrderStatusId == reviewing.Id && row.PossibleStatusId == cancelled.Id))
    {
        context.Transitions.Add(new OrderStatusTransition
        {
            OrderStatusId = reviewing.Id,
            PossibleStatusId = cancelled.Id,
        });
    }

    if (!await context.History.AnyAsync(row => row.OrderId == 1))
    {
        context.History.Add(new OrderStatusHistory
        {
            OrderId = 1,
            OrderStatusId = reviewing.Id,
        });
    }

    await context.SaveChangesAsync();
}

static async Task<OrderStatus> EnsureStatusAsync(OrderStatusDbContext context, string name)
{
    var status = await context.Statuses.SingleOrDefaultAsync(row => row.Name == name);
    if (status is not null)
    {
        return status;
    }

    status = new OrderStatus { Name = name };
    context.Statuses.Add(status);
    await context.SaveChangesAsync();
    return status;
}

static async Task SeedCustomerAsync(CustomerDbContext context)
{
    var customer = await context.Customers.SingleOrDefaultAsync(
        row => row.Email == LegacyTopology.LocalCustomerEmail);
    if (customer is null)
    {
        customer = new Customer
        {
            FirstName = "Local",
            LastName = "Customer",
            Email = LegacyTopology.LocalCustomerEmail,
            Telephone = "local-only",
            Mobile = "local-only",
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
    }

    if (customer.Id != 1)
    {
        throw new InvalidOperationException(
            $"The local customer profile ID {customer.Id} does not match the local identity database ID 1.");
    }
}

static async Task SeedIdentityAsync(
    LegacyIdentityDbContext context,
    string id,
    string email,
    int databaseId,
    bool includeCustomerFields)
{
    var normalizedEmail = email.ToUpperInvariant();
    if (await context.Users.AsNoTracking().AnyAsync(row => row.NormalizedUserName == normalizedEmail))
    {
        return;
    }

    var row = new LegacyIdentityRow
    {
        Id = id,
        UserName = email,
        NormalizedUserName = normalizedEmail,
        Email = email,
        NormalizedEmail = normalizedEmail,
        EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        DatabaseID = databaseId,
        FaxNumber = includeCustomerFields ? "local-only" : null,
        MobileNumber = includeCustomerFields ? "local-only" : null,
        LockoutEnabled = true
    };
    row.PasswordHash = new PasswordHasher<LegacyIdentityRow>()
        .HashPassword(row, LegacyTopology.LocalIdentityPassword);
    context.Users.Add(row);
    await context.SaveChangesAsync();
}
