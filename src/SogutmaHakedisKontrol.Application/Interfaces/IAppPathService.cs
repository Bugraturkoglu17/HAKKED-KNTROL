namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>Uygulamanın veri klasörü. Örn: Masaüstü\SOĞUTMA HAKEDİŞ KONTROL</summary>
public interface IAppPathService
{
    string DataRootPath { get; }
    string DatabasePath { get; }
}
