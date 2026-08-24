using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Domain.Models;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IDisciplineProfileService
{
    /// <summary>Tüm disiplin profillerini (Yangın, Klima, Asansör, Soğutma) döner.</summary>
    IReadOnlyList<DisciplineProfile> GetAll();

    /// <summary>RouteName ile (örn. "yangin") eşleşen profili döner, yoksa null.</summary>
    DisciplineProfile? GetByRoute(string routeName);

    /// <summary>
    /// "Desktop\SERVİS OTOMASYONU DATABASE" altında ortak ve disiplin bazlı
    /// klasör iskeletini oluşturur. Mevcut veri kök yolunu DEĞİŞTİRMEZ —
    /// sadece ileride kullanılacak klasör yapısını önceden hazırlar.
    /// </summary>
    void EnsureFolderSkeleton();

    /// <summary>Desktop\SERVİS OTOMASYONU DATABASE</summary>
    string GetRootPath();

    /// <summary>{RootPath}\Common</summary>
    string GetCommonPath();

    /// <summary>Desktop\SERVİS OTOMASYONU DATABASE\Disciplines\{DataFolderName}</summary>
    string GetDisciplineRoot(MechanicalDiscipline discipline);

    /// <summary>{DisciplineRoot}\MasterData</summary>
    string GetMasterDataPath(MechanicalDiscipline discipline);

    /// <summary>{DisciplineRoot}\Uploads</summary>
    string GetUploadsPath(MechanicalDiscipline discipline);

    /// <summary>{DisciplineRoot}\Uploads\ServiceForms</summary>
    string GetServiceFormsPath(MechanicalDiscipline discipline);

    /// <summary>{DisciplineRoot}\Uploads\Invoices</summary>
    string GetInvoicesPath(MechanicalDiscipline discipline);

    /// <summary>{DisciplineRoot}\Exports</summary>
    string GetExportsPath(MechanicalDiscipline discipline);

    /// <summary>{DisciplineRoot}\Hakedişler</summary>
    string GetProgressClaimsPath(MechanicalDiscipline discipline);

    /// <summary>{DisciplineRoot}\Archive</summary>
    string GetArchivePath(MechanicalDiscipline discipline);

    /// <summary>{DisciplineRoot}\ReferenceTables</summary>
    string GetReferenceTablesPath(MechanicalDiscipline discipline);
}
