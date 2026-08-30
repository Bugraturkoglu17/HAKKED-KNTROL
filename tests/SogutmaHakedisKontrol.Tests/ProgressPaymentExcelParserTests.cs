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

    /// <summary>Gerçek bir olayda yakalanan CİDDİ hata: İlave İşler hakediş dosyasında "SIRA NO" (A
    /// sütunu, yalnızca 1,2,3... satır indeksi) "BAKIM FORM NO" (F sütunu, gerçek fiziksel form
    /// numarası) sütunundan ÖNCE geliyordu. Eskiden ikisi aynı eşleştirme grubunda olduğu için
    /// (ör. "sirano" da "bakimformno" sayılıyordu) TryAdd SIRA NO'yu kilitleyip gerçek form no hiç
    /// okunmuyordu — bu da TÜM hakedişin (144 kalemin tamamı) form numarasıyla eşleşmesini imkansız
    /// hale getirip her satırı "Form Eksik"/"Form No Yerine Mağazadan Eşleşti" olarak işaretliyordu.
    /// SIRA NO artık yalnızca gerçek bir form no kolonu HİÇ bulunamazsa son çare olarak kullanılır.</summary>
    [Fact]
    public void SiraNoSutunu_GercekFormNoSutunuVarken_HicKullanilmaz()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("MALZ HAKEDIS");

        int headerRow = 8;
        ws.Cell(headerRow, 1).Value = "SIRA NO";       // A — yanıltıcı, gerçek olayda F'den önce
        ws.Cell(headerRow, 2).Value = "MAĞAZA KODU";
        ws.Cell(headerRow, 3).Value = "MAĞAZA ADI";
        ws.Cell(headerRow, 4).Value = "FORMAT";
        ws.Cell(headerRow, 5).Value = "TARİH";
        ws.Cell(headerRow, 6).Value = "BAKIM FORM NO"; // F — asıl gerçek form no
        ws.Cell(headerRow, 7).Value = "MALZEME KODU";
        ws.Cell(headerRow, 8).Value = "MALZEME ADI";
        ws.Cell(headerRow, 9).Value = "MİKTARI";
        ws.Cell(headerRow, 10).Value = "BİRİMİ";
        ws.Cell(headerRow, 11).Value = "FİYAT";
        ws.Cell(headerRow, 12).Value = "TOPLAM";

        int dataRow = headerRow + 1;
        ws.Cell(dataRow, 1).Value = 1; // SIRA NO — yalnızca satır indeksi
        ws.Cell(dataRow, 2).Value = "383";
        ws.Cell(dataRow, 3).Value = "G.O.PAŞA ANKARA MM MİGROS";
        ws.Cell(dataRow, 4).Value = "MM";
        ws.Cell(dataRow, 5).Value = new DateTime(2026, 4, 25);
        ws.Cell(dataRow, 6).Value = "19060"; // BAKIM FORM NO — gerçek fiziksel form numarası
        ws.Cell(dataRow, 7).Value = "S1";
        ws.Cell(dataRow, 8).Value = "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ";
        ws.Cell(dataRow, 9).Value = 1;
        ws.Cell(dataRow, 10).Value = "set";
        ws.Cell(dataRow, 11).Value = 2750;
        ws.Cell(dataRow, 12).Value = 2750;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var preview = ProgressPaymentExcelParser.Parse(ms, "test.xlsx");

        Assert.Empty(preview.Errors);
        var item = Assert.Single(preview.Items);
        Assert.Equal("19060", item.MaintenanceFormNo); // "1" (SIRA NO) DEĞİL — gerçek form no
    }
}
