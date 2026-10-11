# Configurable Billing Refresh Window

The billing refresh window is controlled from `App.config` using one setting:

```xml
<add key="BillingRefreshDays" value="2" />
```

This same value controls BOTH:

1. The number of days of existing `billing_transactions` data deleted.
2. The number of days of source data selected by the billing INSERT query.

Examples:

- `2` = last 2 days
- `7` = last 7 days
- `30` = last 30 days
- `90` = last 90 days

No SQL code change is required when the window changes. Change `BillingRefreshDays` in `App.config` and rebuild/deploy the utility.

The DELETE and INSERT remain inside the same transaction. If the INSERT fails, the DELETE is rolled back.

# Dealer Contracts Refresh

At the start of every run, `dealer_contracts` can be reloaded from the contracts Excel sheet:

```xml
<add key="RefreshDealerContracts" value="Y" />
<add key="DealerContractsFilePath" value="C:\...\Inputs\Hierarchy.xlsx" />
<add key="DealerContractsSheetName" value="Sheet1" />
```

- `RefreshDealerContracts`: `Y` = reload before staging; any other value = skip.
- `DealerContractsFilePath`: full path to the `.xlsx`. A relative path is resolved against the utility's folder.
- `DealerContractsSheetName`: optional; blank = first sheet.

Only three columns are loaded, found by header name: `DealerId`, `Contract Name` and `ClientName`. Rows without a DealerId are skipped, and cells showing `#N/A` load as NULL.

All existing rows are deleted and the sheet rows are inserted in one transaction. If the file is missing, a header can't be found, the sheet has no rows, or the INSERT fails, the previous data is kept and the run stops with exit code 1.
