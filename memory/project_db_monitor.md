---
name: project-db-monitor
description: ASP.NET Core 10 multi-db monitoring web app — MSSQL + PostgreSQL, provider pattern, dark UI
metadata:
  type: project
---

ASP.NET Core 10 çok veritabanı izleme uygulaması. MSSQL ve PostgreSQL destekli, provider pattern ile genişletilebilir yapı.

**Why:** Birden fazla veritabanı sunucusunu tek arayüzden izlemek için.

**How to apply:** Yeni provider eklerken `IDatabaseProvider` arayüzünü implement et, `DatabaseProviderFactory`'ye kayıt et.

## Uygulanan Özellikler (Mayıs 2026)
- Bağlantı yönetimi: ekle, sil, aç/kapat, test, **düzenle (Edit modal)**
- DB listeleme: boyut, tablo sayısı, toplam kayıt
- DB detay sayfası (8 sekme):
  - Tablolar — satır/boyut/son sorgu, sütun detay modal (AJAX), tablo sorgu drill-down
  - Oturumlar — aktif session listesi (sys.dm_exec_sessions / pg_stat_activity)
  - Uzun Sorgular — 30sn+ çalışan sorgular
  - Blocking — kilitli/kilitleyen sorgu çiftleri
  - İndeksler — fragmentation %, seek/scan istatistikleri
  - Yedekleme — MSSQL msdb yedek geçmişi, PG WAL archiver durumu
  - Son SQL — execution count, avg/total ms
  - Grafik — boyut bar + kayıt doughnut chart
- Otomatik yenileme toggle (30 sn countdown)
- CSV export (tablo listesi)
- Sütun detay modal (AJAX, GetColumns endpoint)

## Mimari
- `IDatabaseProvider` arayüzü: 11 metod
- `MsSqlProvider` + `PostgreSqlProvider`
- `ConnectionStore` — JSON dosya tabanlı (connections.json), thread-safe
- `DatabaseProviderFactory` — connectionId'ye göre provider döner
- `DatabaseDetailsViewModel` — `WarningMessages` dict ile per-section hata yönetimi

## Eksik kalan özellikler
- MySQL/MariaDB provider (provider pattern hazır)
- Kimlik doğrulama (şu an login yok)
- Connection string şifreleme (plain text JSON)
- Threshold bazlı e-posta/webhook uyarıları
- Tablo boyutu geçmiş takibi (snapshot)
