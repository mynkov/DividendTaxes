using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

var keyFile = "key.txt";
if (!File.Exists(keyFile))
    Console.WriteLine("Enter authorization bearer header from  https://lkfl2.nalog.ru (exapmple: '2e52e67a-38b1-49b6-9e02-160b61cb60aa'):");

var authorizationBearerHeader = File.Exists(keyFile) ? File.ReadAllText(keyFile) : Console.ReadLine();

var ibDividendReportPath = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csv").Single();

var lines = File.ReadAllLines(ibDividendReportPath);
var dividendLines = lines.Where(x => x.StartsWith("Dividends")).Skip(1);
var taxLines = lines.Where(x => x.StartsWith("Withholding Tax")).Skip(1);



var taxes = new List<Tax>();

var taxesTotalAmount = 0M;

foreach (var taxLine in taxLines)
{
    var lineItems = taxLine.Split(',');

    if (lineItems[2] == "Total")
    {
        var expectedTotalAmount = decimal.Parse(lineItems[5]);
        if (expectedTotalAmount != taxesTotalAmount)
            throw new Exception($"Taxes. ExpectedTotalAmount '{expectedTotalAmount}' not equal totalAmount '{taxesTotalAmount}'");
    }
    else
    {
        var currency = lineItems[2];
        var dateLine = lineItems[3];
        var date = DateOnly.Parse(dateLine);
        var description = lineItems[4];
        var amount = decimal.Parse(lineItems[5]);
        var ticker = description.Split('(').First();

        var match = Regex.Match(description, @"^.* - (?<country>.*) Tax$");
        var country = match.Groups["country"].Value;

        if (currency != "USD")
            throw new Exception($"Unknown currency '{currency}'");

        taxes.Add(new Tax(date, ticker, -amount, currency, description, country));

        taxesTotalAmount += amount;
    }
}

var dividendsTotalAmount = 0M;
var incomeAmountRubTotal = 0.0;
var paymentAmountRubTotal = 0.0;
var additionalPaymentAmountRubTotal = 0.0;

var incomeAmountTotal = 0.0;
var paymentAmountTotal = 0.0;
var additionalPaymentAmountTotal = 0.0;

var lkDividends = new List<Dividend>();
var counter = 0;

var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authorizationBearerHeader);

foreach (var dividendLine in dividendLines)
{
    var lineItems = dividendLine.Split(',');

    if(lineItems[2] == "Total")
    {
        var expectedTotalAmount = decimal.Parse(lineItems[5]);
        if (expectedTotalAmount != dividendsTotalAmount)
            throw new Exception($"Dividends. ExpectedTotalAmount '{expectedTotalAmount}' not equal totalAmount '{dividendsTotalAmount}'");
    }
    else
    {
        var currency = lineItems[2];
        var dateLine = lineItems[3];
        var date = DateOnly.Parse(dateLine);
        var description = lineItems[4];
        var amount = decimal.Parse(lineItems[5]);
        var ticker = description.Split('(').First();
        var isInteractiveBrokers = description.Contains("Cash Dividend USD") || description.Contains("Payment in Lieu of Dividend");

        if (currency != "USD")
            throw new Exception($"Unknown currency '{currency}'");

        var tax = taxes.SingleOrDefault(x => x.date == date && x.ticker == ticker && x.description.Contains(description.Replace(" (Ordinary Dividend)", "")));

        if(tax == null)
        {
            Console.WriteLine($"{date} {ticker}: Without taxes");
        }
        else
        {
            if(tax.amount < 0)
                throw new Exception($"{date} {ticker}: tax amount less than 0");
            
            var taxProportion = tax.amount / amount;
            if (taxProportion < 0.08M || taxProportion > 0.11M)
            {
                Console.WriteLine($"{date} {ticker}: Unusual amount of taxes. Country: {tax.country}, Amount: {amount}, Tax: {tax.amount}, Proportion: {taxProportion:P}");
            }
        }

        dividendsTotalAmount += amount;

        var sourceCountryCode = GetCountryCode(tax != null ? tax.country : GetCountry(ticker));
        var paymentCountryCode = isInteractiveBrokers ? 
            "840" : // USA
            "643"; // Russia

        var currencyCode = "840"; // $ USA
        var taxRate = 13;
        var incomeTypeCode = 1010; // Dividends
        var magicNumber = 1;
        var incomeSourceName = isInteractiveBrokers ? $"Interactive Brokers LLC ({ticker})" : $"Тинькофф Банк ({ticker})";
        var id = $"incomeSourcekyq1dq501{counter++}";


        var dateFormat = "yyyy-MM-dd";
        var incomeDate = date;
        var incomeDateString = incomeDate.ToString(dateFormat);

        //continue;

        var incomeDateResponse = await httpClient.GetStringAsync($"https://lkfl2.nalog.ru/taps/api/v1/dictionary/currency-rates?code={currencyCode}&date={incomeDateString}");
        var incomeDateCurrencyRate = JsonConvert.DeserializeObject<CurrencyRate>(incomeDateResponse);
        var incomeAmount = (double)amount;
        incomeAmountTotal += incomeAmount;
        var incomeAmountRub = Math.Round(incomeDateCurrencyRate.Rate * incomeAmount, 2);
        incomeAmountRubTotal += incomeAmountRub;

        var fullTaxRub = Math.Round(incomeAmountRub * ((double)taxRate / 100), 2);
        var fullTax = Math.Round(incomeAmount * ((double)taxRate / 100), 2);

        if (tax != null)
        {
            var taxPaymentDate = tax.date;
            var taxPaymentDateString = taxPaymentDate.ToString(dateFormat);

            var taxPaymentDateResponse = await httpClient.GetStringAsync($"https://lkfl2.nalog.ru/taps/api/v1/dictionary/currency-rates?code={currencyCode}&date={taxPaymentDateString}");
            var taxPaymentDateCurrencyRate = JsonConvert.DeserializeObject<CurrencyRate>(taxPaymentDateResponse);
            var paymentAmount = (double)tax.amount;
            var paymentAmountRub = Math.Round(taxPaymentDateCurrencyRate.Rate * paymentAmount, 2);
            paymentAmountRubTotal += paymentAmountRub;
            paymentAmountTotal += paymentAmount;

            var lkDividend = new Dividend
            {
                Id = id,
                IncomeDate = incomeDateString,
                TaxPaymentDate = taxPaymentDateString,
                IncomeSum = incomeAmountRub,
                OksmIst = sourceCountryCode,
                OksmZach = paymentCountryCode,
                TaxRate = taxRate,
                IncomeSourceName = incomeSourceName,
                CurrencyCode = currencyCode,
                IncomeTypeCode = incomeTypeCode,
                IncomeCurrencyRate = incomeDateCurrencyRate.Rate,
                IncomeCurrencyUnitsRub = incomeDateCurrencyRate.Rate,
                IncomeCurrencyUnits = magicNumber,
                IncomeAmountCurrency = incomeAmount,
                IncomeAmountRub = incomeAmountRub,
                PaymentCurrencyRate = taxPaymentDateCurrencyRate.Rate,
                PaymentCurrencyUnitsRub = taxPaymentDateCurrencyRate.Rate,
                PaymentCurrencyUnits = magicNumber,
                PaymentAmountCurrency = paymentAmount,
                PaymentAmountRub = paymentAmountRub,
                ShareNumerator = magicNumber,
                ShareDenominator = magicNumber,
                MoreDeductions = new List<object>()
            };

            lkDividends.Add(lkDividend);

            var additionalPaymentAmountRub = fullTaxRub - paymentAmountRub;
            if (additionalPaymentAmountRub > 0)
            {
                additionalPaymentAmountRubTotal += additionalPaymentAmountRub;
                additionalPaymentAmountTotal += fullTax - paymentAmount;
            }
            else
            {
                Console.WriteLine($"{date} {ticker}: More than {taxRate}% taxes from county {tax.country}. Income: {incomeAmount}$, {incomeAmountRub} rub. Payment: {paymentAmount}$, {paymentAmountRub} rub");
            }
        }
        else
        {
            var lkDividendWithoutTax = new Dividend
            {
                Id = id,
                IncomeDate = incomeDateString,
                IncomeSum = incomeAmountRub,
                OksmIst = sourceCountryCode,
                OksmZach = paymentCountryCode,
                TaxRate = taxRate,
                IncomeSourceName = incomeSourceName,
                CurrencyCode = currencyCode,
                IncomeTypeCode = incomeTypeCode,
                IncomeCurrencyRate = incomeDateCurrencyRate.Rate,
                IncomeCurrencyUnitsRub = incomeDateCurrencyRate.Rate,
                IncomeCurrencyUnits = magicNumber,
                IncomeAmountCurrency = incomeAmount,
                IncomeAmountRub = incomeAmountRub,
                ShareNumerator = magicNumber,
                ShareDenominator = magicNumber,
                MoreDeductions = new List<object>()
            };

            lkDividends.Add(lkDividendWithoutTax);

            var additionalPaymentAmountRub = fullTaxRub;
            additionalPaymentAmountRubTotal += additionalPaymentAmountRub;
            additionalPaymentAmountTotal += fullTax;

            Console.WriteLine($"{date} {ticker}: Additional tax payment {additionalPaymentAmountRub} rub. Income: {incomeAmount}$, {incomeAmountRub} rub");
        }
    } 
}

var lkDividendsJson = JsonConvert.SerializeObject(lkDividends, Formatting.Indented);

Console.WriteLine($"Income total: {incomeAmountRubTotal} rub, {incomeAmountTotal}$");
Console.WriteLine($"US payment: {paymentAmountRubTotal} rub, {paymentAmountTotal}$");
Console.WriteLine($"RU payment: {additionalPaymentAmountRubTotal} rub, {additionalPaymentAmountTotal}$");
Console.WriteLine($"Income after tax: {incomeAmountRubTotal - paymentAmountRubTotal - additionalPaymentAmountRubTotal} rub, {incomeAmountTotal - paymentAmountTotal - additionalPaymentAmountTotal}$");
Console.WriteLine(lkDividendsJson);
Console.WriteLine("Send to:");
Console.WriteLine("https://lkfl2.nalog.ru/taps/api/v1/notification/1151020/2021/save");
File.WriteAllText("output.txt", lkDividendsJson);

Console.ReadLine();


static string GetCountry(string ticker) => ticker switch
{
    "BTI" => "GB",
    "UL" => "GB",
    "GSK" => "GB",
    "VOD" => "GB",
    "AZN" => "GB",
    "RDS B" => "GB",
    "SHEL" => "GB",
    "TCS" => "CY",
    "AGRO" => "CY",
    "GLTR" => "CY",
    "POLY" => "JE",
    "VIV" => "BR",
    _ => throw new Exception($"Unknown ticker: {ticker}"),
};

static string GetCountryCode(string country) => country switch
{
    "US" => "840", // USA
    "GB" => "826", // UK    
    "CA" => "124", // Canada
    "DE" => "276", // Germany
    "JP" => "392", // Japan
    "NL" => "528", // Netherlands
    "IN" => "356", // India
    "CN" => "156", // China
    "TW" => "158", // Taiwan
    "CY" => "196", // Cyprus
    "JE" => "832", // Jersey
    "BR" => "076", // Brazil
    _ => throw new Exception($"Unknown country: {country}"),
};




public record Tax(DateOnly date, string ticker, decimal amount, string currency, string description, string country);

public partial class CurrencyRate
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("count")]
    public long Count { get; set; }

    [JsonProperty("rate")]
    public double Rate { get; set; }
}


public partial class Dividend
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("vidDohod")]
    public object VidDohod { get; set; }

    [JsonProperty("incomeDate")]
    public string IncomeDate { get; set; }

    [JsonProperty("taxPaymentDate")]
    public string TaxPaymentDate { get; set; }

    [JsonProperty("deductionSum219_1")]
    public object DeductionSum2191 { get; set; }

    [JsonProperty("incomeSum")]
    public double IncomeSum { get; set; }

    [JsonProperty("minOfIncomeAndDeduction")]
    public long MinOfIncomeAndDeduction { get; set; }

    [JsonProperty("applyDeduction")]
    public bool ApplyDeduction { get; set; }

    [JsonProperty("numWorkedMonths")]
    public object NumWorkedMonths { get; set; }

    [JsonProperty("oksmIst")]
    public string OksmIst { get; set; }

    [JsonProperty("oksmZach")]
    public string OksmZach { get; set; }

    [JsonProperty("isHighClassSpecialist")]
    public bool IsHighClassSpecialist { get; set; }

    [JsonProperty("dohodOsv")]
    public long DohodOsv { get; set; }

    [JsonProperty("taxRate")]
    public long TaxRate { get; set; }

    [JsonProperty("oksm")]
    public object Oksm { get; set; }

    [JsonProperty("incomeSourceName")]
    public string IncomeSourceName { get; set; }

    [JsonProperty("currencyCode")]
    public string CurrencyCode { get; set; }

    [JsonProperty("incomeTypeCode")]
    public long IncomeTypeCode { get; set; }

    [JsonProperty("kikNum")]
    public object KikNum { get; set; }

    [JsonProperty("incomeCurrencyRate")]
    public double IncomeCurrencyRate { get; set; }

    [JsonProperty("incomeCurrencyRateRub")]
    public long IncomeCurrencyRateRub { get; set; }

    [JsonProperty("incomeCurrencyUnitsRub")]
    public double IncomeCurrencyUnitsRub { get; set; }

    [JsonProperty("incomeCurrencyUnits")]
    public long IncomeCurrencyUnits { get; set; }

    [JsonProperty("incomeAmountCurrency")]
    public double IncomeAmountCurrency { get; set; }

    [JsonProperty("incomeAmountRub")]
    public double IncomeAmountRub { get; set; }

    [JsonProperty("taxExemptLiquidProp")]
    public long TaxExemptLiquidProp { get; set; }

    [JsonProperty("taxExemptKIKDividend")]
    public long TaxExemptKikDividend { get; set; }

    [JsonProperty("determProcedureKIK")]
    public object DetermProcedureKik { get; set; }

    [JsonProperty("paymentCurrencyRate")]
    public object PaymentCurrencyRate { get; set; }

    [JsonProperty("paymentCurrencyRateRub")]
    public object PaymentCurrencyRateRub { get; set; }

    [JsonProperty("paymentCurrencyUnitsRub")]
    public object PaymentCurrencyUnitsRub { get; set; }

    [JsonProperty("paymentCurrencyUnits")]
    public long PaymentCurrencyUnits { get; set; }

    [JsonProperty("paymentAmountCurrency")]
    public double PaymentAmountCurrency { get; set; }

    [JsonProperty("paymentAmountRub")]
    public double PaymentAmountRub { get; set; }

    [JsonProperty("taxForeignIncomeSum")]
    public long TaxForeignIncomeSum { get; set; }

    [JsonProperty("estimatedTax")]
    public object EstimatedTax { get; set; }

    [JsonProperty("estimatedReduction")]
    public object EstimatedReduction { get; set; }

    [JsonProperty("shareNumerator")]
    public long ShareNumerator { get; set; }

    [JsonProperty("shareDenominator")]
    public long ShareDenominator { get; set; }

    [JsonProperty("taxDeductionCode")]
    public long TaxDeductionCode { get; set; }

    [JsonProperty("deductionSum")]
    public long DeductionSum { get; set; }

    [JsonProperty("moreDeductions")]
    public List<object> MoreDeductions { get; set; }

    [JsonProperty("sumOfFixedAdvancePayments")]
    public object SumOfFixedAdvancePayments { get; set; }

    [JsonProperty("sumOfCorporateIncomeTax")]
    public object SumOfCorporateIncomeTax { get; set; }

    [JsonProperty("sumOfMaterialBenefitToRefund")]
    public object SumOfMaterialBenefitToRefund { get; set; }
}

