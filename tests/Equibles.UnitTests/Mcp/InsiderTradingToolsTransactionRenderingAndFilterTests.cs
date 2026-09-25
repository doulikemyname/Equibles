using Equibles.CommonStocks.Data;
using Equibles.CommonStocks.Data.Models;
using Equibles.CommonStocks.Repositories;
using Equibles.CorporateActions.Data;
using Equibles.CorporateActions.Repositories;
using Equibles.Data;
using Equibles.Errors.BusinessLogic;
using Equibles.Errors.Data;
using Equibles.Errors.Repositories;
using Equibles.InsiderTrading.Data;
using Equibles.InsiderTrading.Data.Models;
using Equibles.InsiderTrading.Mcp.Tools;
using Equibles.InsiderTrading.Repositories;
using Equibles.Media.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Equibles.UnitTests.Mcp;

/// <summary>
/// Pins the GetInsiderTransactions rendering and filter contract introduced by the MCP
/// audit: the Buy/Sell labels are reserved for the open-market Purchase/Sale codes and
/// every other SEC code renders its own meaning (a Conversion or Tax Payment reported
/// as "Buy"/"Sell" told the model an open-market trade happened that never did); the
/// Rule 10b5-1 plan flag is surfaced; degenerate zero-share/zero-balance rows are
/// dropped; and the date-range/transactionType arguments reject unparseable values
/// instead of silently ignoring them.
/// </summary>
public class InsiderTradingToolsTransactionRenderingAndFilterTests
{
    private static EquiblesFinancialDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<EquiblesFinancialDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .EnableServiceProviderCaching(false)
            .Options;
        var ctx = new EquiblesFinancialDbContext(
            options,
            new IModuleConfiguration[]
            {
                new CommonStocksModuleConfiguration(),
                new CorporateActionsModuleConfiguration(),
                new InsiderTradingModuleConfiguration(),
                new ErrorsModuleConfiguration(),
                // InsiderFiling navigates to Media's File (Content), so the model pulls the File
                // entity in and needs Media's configuration (the StorageProvider conversion) or
                // model finalization fails.
                new MediaModuleConfiguration(),
            }
        );
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private static InsiderTradingTools Sut(EquiblesFinancialDbContext db) =>
        new(
            new InsiderTransactionRepository(db),
            new InsiderOwnerRepository(db),
            new Form144FilingRepository(db),
            new CommonStockRepository(db),
            new StockSplitRepository(db),
            new ErrorManager(new ErrorRepository(db)),
            Substitute.For<ILogger<InsiderTradingTools>>()
        );

    private static CommonStock NewStock() =>
        new()
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            Cik = "0000320193",
        };

    private static InsiderOwner NewOwner(string name = "John Doe", string cik = "0001234567") =>
        new()
        {
            OwnerCik = cik,
            Name = name,
            IsDirector = true,
        };

    private static InsiderTransaction NewTransaction(
        CommonStock stock,
        InsiderOwner owner,
        TransactionCode code,
        AcquiredDisposed acquiredDisposed,
        string accessionNumber,
        DateOnly? transactionDate = null,
        long shares = 1000,
        decimal pricePerShare = 100m,
        long sharesOwnedAfter = 5000,
        bool? isRule10b5One = null
    ) =>
        new()
        {
            CommonStockId = stock.Id,
            CommonStock = stock,
            InsiderOwnerId = owner.Id,
            InsiderOwner = owner,
            TransactionDate = transactionDate ?? new DateOnly(2024, 6, 1),
            FilingDate = (transactionDate ?? new DateOnly(2024, 6, 1)).AddDays(2),
            TransactionCode = code,
            Shares = shares,
            PricePerShare = pricePerShare,
            AcquiredDisposed = acquiredDisposed,
            SharesOwnedAfter = sharesOwnedAfter,
            OwnershipNature = OwnershipNature.Direct,
            SecurityTitle = "Common Stock",
            AccessionNumber = accessionNumber,
            IsRule10b5One = isRule10b5One,
        };

    [Fact]
    public async Task GetInsiderTransactions_ConversionRow_RendersConversionNotBuy()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Conversion,
                AcquiredDisposed.Acquired,
                "acc-1"
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL");

        output.Should().Contain("| Conversion |");
        output.Should().NotContain("| Buy |");
    }

    [Fact]
    public async Task GetInsiderTransactions_TaxPaymentRow_RendersTaxPaymentNotSell()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.TaxPayment,
                AcquiredDisposed.Disposed,
                "acc-1"
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL");

        output.Should().Contain("| Tax Payment |");
        output.Should().NotContain("| Sell |");
    }

    [Fact]
    public async Task GetInsiderTransactions_Rule10b5OneFlag_RendersYesNoAndDash()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Sale,
                AcquiredDisposed.Disposed,
                "acc-1",
                transactionDate: new DateOnly(2024, 6, 3),
                isRule10b5One: true
            )
        );
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Sale,
                AcquiredDisposed.Disposed,
                "acc-2",
                transactionDate: new DateOnly(2024, 6, 2),
                isRule10b5One: false
            )
        );
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Sale,
                AcquiredDisposed.Disposed,
                "acc-3",
                transactionDate: new DateOnly(2024, 6, 1),
                isRule10b5One: null
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL");

        output.Should().Contain("| 10b5-1 |");
        output.Should().Contain("| Yes |");
        output.Should().Contain("| No |");
        output.Should().Contain("| - |");
    }

    [Fact]
    public async Task GetInsiderTransactions_DefaultMode_PreservesExistingSchema()
    {
        await using var db = NewDb();
        CommonStock stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "0001234567-24-000089"
            )
        );
        await db.SaveChangesAsync();

        var sut = Sut(db);
        var output = await sut.GetInsiderTransactions("AAPL");
        var explicitDefault = await sut.GetInsiderTransactions("AAPL", includeProvenance: false);
        var explicitOffsetZero = await sut.GetInsiderTransactions("AAPL", offset: 0);

        output.Should().Be(explicitDefault);
        output.Should().Be(explicitOffsetZero);
        output
            .Should()
            .Contain(
                "| Date | Insider | Role | Type | Shares | Price | Value | Owned After | Security | Ownership | 10b5-1 |"
            );
        output.Should().NotContain("| Filing Date |");
        output.Should().NotContain("Common Stock ID");
        output.Should().NotContain(stock.Id.ToString());
        output.Should().NotContain("0001234567-24-000089");
    }

    [Fact]
    public async Task GetInsiderTransactions_ProvenanceMode_RendersSourceFilingFacts()
    {
        await using var db = NewDb();
        CommonStock stock = NewStock();
        var owner = NewOwner(name: "Jane | Doe", cik: "0007654321");
        db.AddRange(stock, owner);
        var transaction = NewTransaction(
            stock,
            owner,
            TransactionCode.Purchase,
            AcquiredDisposed.Disposed,
            "0001234567-24-000089",
            transactionDate: new DateOnly(2024, 6, 1),
            shares: 1250,
            pricePerShare: 12.34m,
            sharesOwnedAfter: 8750,
            isRule10b5One: false
        );
        transaction.FilingDate = new DateOnly(2024, 6, 4);
        transaction.FilingForm = InsiderOwnershipForm.Form4;
        transaction.IsAmendment = true;
        transaction.OriginalFilingDate = new DateOnly(2024, 6, 3);
        transaction.SupersededAccessionNumber = "0001234567-24-000042";
        transaction.SecurityKind = InsiderSecurityKind.NonDerivative;
        transaction.OwnershipNature = OwnershipNature.Indirect;
        db.Add(transaction);
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL", includeProvenance: true);

        output
            .Should()
            .Contain(
                "| Transaction Date | Common Stock ID | Filing Date | Filing Form | Accession Number | Is Amendment | Original Filing Date | Superseded Accession Number | Owner CIK | Insider | Role | Type | Transaction Code | Acquired / Disposed | Shares | Price | Value | Owned After | Security | Security Kind | Ownership | 10b5-1 |"
            );
        output
            .Should()
            .Contain(
                $"| 2024-06-01 | {transaction.CommonStockId} | 2024-06-04 | Form 4/A | 0001234567-24-000089 | Yes | 2024-06-03 | 0001234567-24-000042 | 0007654321 | Jane \\| Doe | Director | Buy | Purchase | Disposed | 1,250 | $12.34 | $15,425 | 8,750 | Common Stock | Non-derivative | Indirect | No |"
            );
        output.Should().NotContain("Creation Time");
        output.Should().NotContain(transaction.CreationTime.ToString("O"));
    }

    [Fact]
    public async Task GetInsiderTransactions_ProvenanceMode_RendersMissingOptionalFactsDeterministically()
    {
        await using var db = NewDb();
        CommonStock stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        var transaction = NewTransaction(
            stock,
            owner,
            TransactionCode.Gift,
            AcquiredDisposed.Acquired,
            "0001234567-24-000090",
            isRule10b5One: null
        );
        transaction.FilingForm = InsiderOwnershipForm.Unknown;
        transaction.IsAmendment = false;
        transaction.OriginalFilingDate = null;
        transaction.SupersededAccessionNumber = null;
        db.Add(transaction);
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL", includeProvenance: true);

        output
            .Should()
            .Contain(
                $"| 2024-06-01 | {transaction.CommonStockId} | 2024-06-03 | Unknown | 0001234567-24-000090 | No | - | - | 0001234567 | John Doe | Director | Gift | Gift | Acquired | 1,000 | $100.00 | $100,000 | 5,000 | Common Stock | Unknown | Direct | - |"
            );
    }

    [Fact]
    public async Task GetInsiderTransactions_ProvenanceMode_PreservesTickerLimitAndReadOnlyBehaviour()
    {
        await using var db = NewDb();
        CommonStock stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "older-accession",
                transactionDate: new DateOnly(2024, 5, 1)
            )
        );
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Sale,
                AcquiredDisposed.Disposed,
                "newer-accession",
                transactionDate: new DateOnly(2024, 6, 1)
            )
        );
        await db.SaveChangesAsync();
        var transactionCount = await db.Set<InsiderTransaction>().CountAsync();

        var output = await Sut(db)
            .GetInsiderTransactions("aapl", maxResults: 1, includeProvenance: true);

        output.Should().Contain("Apple Inc. (AAPL)");
        output.Should().Contain("newer-accession");
        output.Should().NotContain("older-accession");
        (await db.Set<InsiderTransaction>().CountAsync()).Should().Be(transactionCount);
    }

    [Fact]
    public async Task GetInsiderTransactions_ZeroShareZeroBalanceRow_IsDropped()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var real = NewOwner(name: "Real Trader", cik: "0000000001");
        var ghost = NewOwner(name: "Ghost Filer", cik: "0000000002");
        db.AddRange(stock, real, ghost);
        db.Add(
            NewTransaction(
                stock,
                real,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-1"
            )
        );
        // A parser artifact / no-securities Form 3: zero shares, zero price, zero balance.
        db.Add(
            NewTransaction(
                stock,
                ghost,
                TransactionCode.Other,
                AcquiredDisposed.Acquired,
                "acc-2",
                shares: 0,
                pricePerShare: 0m,
                sharesOwnedAfter: 0
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL");

        output.Should().Contain("Real Trader");
        output.Should().NotContain("Ghost Filer");
        output.Should().Contain("Showing 1 most recent transactions");
    }

    [Fact]
    public async Task GetInsiderTransactions_InvalidFromDate_ReturnsStrictError()
    {
        await using var db = NewDb();
        var stock = NewStock();
        db.Add(stock);
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL", fromDate: "June 2024");

        output.Should().Be("Unknown fromDate 'June 2024'. Accepted: yyyy-MM-dd.");
    }

    [Fact]
    public async Task GetInsiderTransactions_InvalidTransactionType_ListsAcceptedValues()
    {
        await using var db = NewDb();
        var stock = NewStock();
        db.Add(stock);
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL", transactionType: "Bought");

        output
            .Should()
            .Be(
                "Unknown transactionType 'Bought'. Accepted: Buy, Sell, Award, Conversion, Exercise, TaxPayment, Expiration, Gift, Inheritance, Discretionary, Other."
            );
    }

    [Fact]
    public async Task GetInsiderTransactions_DateRange_LimitsToWindow()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-1",
                transactionDate: new DateOnly(2024, 1, 15)
            )
        );
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-2",
                transactionDate: new DateOnly(2024, 3, 15)
            )
        );
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-3",
                transactionDate: new DateOnly(2024, 6, 15)
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db)
            .GetInsiderTransactions("AAPL", fromDate: "2024-02-01", toDate: "2024-05-01");

        output.Should().Contain("2024-03-15");
        output.Should().NotContain("2024-01-15");
        output.Should().NotContain("2024-06-15");
    }

    [Fact]
    public async Task GetInsiderTransactions_TransactionTypeBuy_FiltersToOpenMarketPurchases()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-1"
            )
        );
        db.Add(
            NewTransaction(stock, owner, TransactionCode.Sale, AcquiredDisposed.Disposed, "acc-2")
        );
        db.Add(
            NewTransaction(stock, owner, TransactionCode.Award, AcquiredDisposed.Acquired, "acc-3")
        );
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL", transactionType: "Buy");

        output.Should().Contain("| Buy |");
        output.Should().NotContain("| Sell |");
        output.Should().NotContain("| Award |");
    }

    [Fact]
    public async Task GetInsiderTransactions_Truncated_AppendsTruncationNote()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        for (var i = 0; i < 3; i++)
        {
            db.Add(
                NewTransaction(
                    stock,
                    owner,
                    TransactionCode.Purchase,
                    AcquiredDisposed.Acquired,
                    $"acc-{i}",
                    transactionDate: new DateOnly(2024, 1 + i, 1)
                )
            );
        }
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL", maxResults: 2);

        output
            .Should()
            .Contain(
                "Showing results 1-2 of 3 - raise maxResults (max 500) or pass offset=2 to continue."
            );
    }

    [Fact]
    public async Task GetInsiderTransactions_Offset_ReturnsNextPageWithDefaultSchema()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "old",
                transactionDate: new DateOnly(2024, 1, 1)
            )
        );
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "middle",
                transactionDate: new DateOnly(2024, 2, 1)
            )
        );
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "new",
                transactionDate: new DateOnly(2024, 3, 1)
            )
        );
        await db.SaveChangesAsync();

        var firstPage = await Sut(db).GetInsiderTransactions("AAPL", maxResults: 1);
        var secondPage = await Sut(db).GetInsiderTransactions("AAPL", maxResults: 1, offset: 1);

        firstPage.Should().Contain("2024-03-01").And.NotContain("2024-02-01");
        secondPage
            .Should()
            .Contain("2024-02-01")
            .And.NotContain("2024-03-01")
            .And.NotContain("2024-01-01");
        secondPage.Should().Contain("Showing transactions 2-2 of 3");
        secondPage
            .Should()
            .Contain(
                "| Date | Insider | Role | Type | Shares | Price | Value | Owned After | Security | Ownership | 10b5-1 |"
            );
        secondPage.Should().NotContain("Common Stock ID");
    }

    [Fact]
    public async Task GetInsiderTransactions_Offset_ReturnsFinalPartialPage()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        for (var month = 1; month <= 3; month++)
        {
            db.Add(
                NewTransaction(
                    stock,
                    owner,
                    TransactionCode.Purchase,
                    AcquiredDisposed.Acquired,
                    $"acc-{month}",
                    transactionDate: new DateOnly(2024, month, 1)
                )
            );
        }
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL", maxResults: 2, offset: 2);

        output
            .Should()
            .Contain("2024-01-01")
            .And.NotContain("2024-02-01")
            .And.NotContain("2024-03-01");
        output.Should().Contain("Showing results 3-3 of 3 (the last page).");
    }

    [Fact]
    public async Task GetInsiderTransactions_OffsetPastEnd_ReturnsExplicitError()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-1"
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL", offset: 1);

        output
            .Should()
            .Be("No results at offset 1 - only 1 insider transactions match; lower offset.");
    }

    [Fact]
    public async Task GetInsiderTransactions_OffsetPaging_TiedDatesSplitDeterministically()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var ownerA = NewOwner("Owner A", "0000000001");
        var ownerB = NewOwner("Owner B", "0000000002");
        var ownerC = NewOwner("Owner C", "0000000003");
        var ownerD = NewOwner("Owner D", "0000000004");
        db.AddRange(stock, ownerA, ownerB, ownerC, ownerD);
        var transactionDate = new DateOnly(2024, 6, 1);
        db.Add(
            NewTransaction(
                stock,
                ownerD,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-d",
                transactionDate
            )
        );
        db.Add(
            NewTransaction(
                stock,
                ownerB,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-b",
                transactionDate
            )
        );
        db.Add(
            NewTransaction(
                stock,
                ownerA,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-a",
                transactionDate
            )
        );
        db.Add(
            NewTransaction(
                stock,
                ownerC,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-c",
                transactionDate
            )
        );
        await db.SaveChangesAsync();

        var firstPage = await Sut(db).GetInsiderTransactions("AAPL", maxResults: 2);
        var secondPage = await Sut(db).GetInsiderTransactions("AAPL", maxResults: 2, offset: 2);

        firstPage.Should().Contain("Owner A").And.Contain("Owner B");
        firstPage.Should().NotContain("Owner C").And.NotContain("Owner D");
        secondPage.Should().Contain("Owner C").And.Contain("Owner D");
        secondPage.Should().NotContain("Owner A").And.NotContain("Owner B");
        firstPage
            .IndexOf("Owner A", StringComparison.Ordinal)
            .Should()
            .BeLessThan(firstPage.IndexOf("Owner B", StringComparison.Ordinal));
        secondPage
            .IndexOf("Owner C", StringComparison.Ordinal)
            .Should()
            .BeLessThan(secondPage.IndexOf("Owner D", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetInsiderTransactions_ProvenanceMode_PagedRowRetainsSourceFilingFacts()
    {
        await using var db = NewDb();
        CommonStock stock = NewStock();
        var owner = NewOwner(name: "Jane Doe", cik: "0007654321");
        db.AddRange(stock, owner);
        var older = NewTransaction(
            stock,
            owner,
            TransactionCode.Purchase,
            AcquiredDisposed.Acquired,
            "older-accession",
            transactionDate: new DateOnly(2024, 5, 1)
        );
        older.FilingDate = new DateOnly(2024, 5, 3);
        older.FilingForm = InsiderOwnershipForm.Form4;
        older.SecurityKind = InsiderSecurityKind.NonDerivative;
        db.Add(older);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Sale,
                AcquiredDisposed.Disposed,
                "newer-accession",
                transactionDate: new DateOnly(2024, 6, 1)
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db)
            .GetInsiderTransactions("AAPL", maxResults: 1, offset: 1, includeProvenance: true);

        output.Should().Contain("| Transaction Date | Common Stock ID | Filing Date |");
        output
            .Should()
            .Contain(
                $"| 2024-05-01 | {older.CommonStockId} | 2024-05-03 | Form 4 | older-accession |"
            );
        output.Should().NotContain("newer-accession");
        output.Should().NotContain("Creation Time");
    }

    [Fact]
    public async Task GetInsiderTransactions_NotTruncated_HasNoTruncationNote()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-1"
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("AAPL");

        output.Should().NotContain("raise maxResults to see more");
    }

    [Fact]
    public async Task GetInsiderTransactions_LowercaseTicker_EchoesCanonicalTicker()
    {
        await using var db = NewDb();
        var stock = NewStock();
        var owner = NewOwner();
        db.AddRange(stock, owner);
        db.Add(
            NewTransaction(
                stock,
                owner,
                TransactionCode.Purchase,
                AcquiredDisposed.Acquired,
                "acc-1"
            )
        );
        await db.SaveChangesAsync();

        var output = await Sut(db).GetInsiderTransactions("aapl");

        output.Should().Contain("Apple Inc. (AAPL)");
        output.Should().NotContain("(aapl)");
    }
}
