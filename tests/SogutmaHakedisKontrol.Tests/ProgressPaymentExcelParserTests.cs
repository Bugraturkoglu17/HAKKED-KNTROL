using ClosedXML.Excel;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>
/// Glikol Kullanım hakediş dosyasının gerçek başlık düzenini ("MAĞZ KODU", "FORM NO",
/// "ONAYA SUNULAN MİKTAR" / "KESİLEN MİKTAR" / "ONAYLANAN MİKTAR") sentetik olarak üretip
/// parser'ın doğru sayfayı bulduğunu ve doğru "miktar" kolonunu (leftmost/first-match) seçtiğini
/// doğrular — daha önce bu başlıklar tanınmadığı için "sayfa bulunamadı" hatası veriliyordu.
/// </summary>
public class ProgressPaymentExcelParserTests
{
    private static byte[] BuildGlikolStyleWorkbook()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("MALZ HAKEDIS");

        int headerRow = 8;
        ws.Cell(headerRow, 1).Value = "MAĞZ KODU";
        ws.Cell(headerRow, 2).Value = "MAĞAZA ADI";
        ws.Cell(headerRow, 3).Value = "FORMAT";
        ws.Cell(headerRow, 4).Value = "TARİH";
        ws.Cell(headerRow, 5).Value = "FORM NO";
        ws.Cell(headerRow, 6).Value = "MALZEME KODU";
        ws.Cell(headerRow, 7).Value = "MALZEME ADI";
        ws.Cell(headerRow, 8).Value = "ONAYA SUNULAN MİKTAR";
        ws.Cell(headerRow, 9).Value = "KESİLEN MİKTAR";
        ws.Cell(headerRow, 10).Value = "FATURA DÖNEMİ";
        ws.Cell(headerRow, 11).Value = "FİYAT";
        ws.Cell(headerRow, 12).Value = "TOPLAM";
        ws.Cell(headerRow, 13).Value = "GENEL TOPLAM";
        ws.Cell(headerRow, 14).Value = "ONAYLANAN MİKTAR";

        int dataRow = headerRow + 1;
        ws.Cell(dataRow, 1).Value = "1234";
        ws.Cell(dataRow, 2).Value = "Test Mağaza";
        ws.Cell(dataRow, 3).Value = "A";
        ws.Cell(dataRow, 4).Value = new DateTime(2026, 6, 15);
        ws.Cell(dataRow, 5).Value = "15527";
        ws.Cell(dataRow, 6).Value = "GLK-001";
        ws.Cell(dataRow, 7).Value = "Glikol Sıvısı";
        ws.Cell(dataRow, 8).Value = 20; // ONAYA SUNULAN MİKTAR — gerçek satır miktarı
        ws.Cell(dataRow, 9).Value = 0;  // KESİLEN MİKTAR
        ws.Cell(dataRow, 11).Value = 150;
        ws.Cell(dataRow, 12).Value = 3000;
        ws.Cell(dataRow, 13).Value = 3000; // GENEL TOPLAM (TL toplamı)
        ws.Cell(dataRow, 14).Value = 3000; // ONAYLANAN MİKTAR — bu dosyada aslında TL tutarı, adet DEĞİL

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public void GlikolBasliklari_DogruSayfayiVeMiktarKolonunuBulur()
    {
        var bytes = BuildGlikolStyleWorkbook();
        using var stream = new MemoryStream(bytes);

        var preview = ProgressPaymentExcelParser.Parse(stream, "glikol_hakedis.xlsx");

        Assert.Empty(preview.Errors);
        Assert.Single(preview.Items);

        var item = preview.Items[0];
        Assert.Equal(20, item.Quantity); // "ONAYLANAN MİKTAR" (3000, aslında TL) DEĞİL, "ONAYA SUNULAN MİKTAR" (20) alınmalı
        Assert.Equal("15527", item.MaintenanceFormNo); // bare "FORM NO" başlığı da tanınmalı
        Assert.Equal("1234", item.StoreCode);
    }
}
