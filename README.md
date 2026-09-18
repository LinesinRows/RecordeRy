# RecordeRy

Windows için hafif bir **replay buffer** (anlık tekrar) kaydedici. Arka planda sürekli çalışır, ekranın son birkaç dakikasını bir tampon halinde tutar ve bir kısayola bastığında o anı MP4 olarak diske kaydeder — NVIDIA ShadowPlay / AMD ReLive'daki "Instant Replay" özelliğine benzer, bağımsız ve açık kaynak bir alternatif.

## Nasıl çalışır?

1. Ekran, DXGI **Desktop Duplication API** ile yakalanır.
2. Yakalanan kareler `ffmpeg`'e ham video olarak aktarılır. Sistemde varsa donanım encoder'ı (**NVENC** / **QSV** / **AMF**) otomatik seçilir; hiçbiri çalışmıyorsa yazılım encoder'ına (`libx264`) düşülür.
3. Kayıt, `%LocalAppData%\RecordeRy\Buffer` altında 2 saniyelik `segment_*.mp4` parçalarına bölünerek yazılır.
4. Arka planda çalışan bir temizleyici, ayarlanan buffer süresinden (varsayılan 1 dakika, 1–30 dakika arası ayarlanabilir) daha eski segmentleri sürekli siler — disk her zaman yalnızca "son N dakika" kadar yer kaplar.
5. Global kısayola (varsayılan **Ctrl+Shift+S**) basıldığında, o ana kadar biriken segmentler `ffmpeg` ile tek bir MP4 dosyasında birleştirilip `Videolar\RecordeRy` klasörüne kaydedilir.
6. Uygulama system tray'de yaşar; pencereyi kapatmak onu sadece tepsiye gizler, arka planda kayda devam eder. Tray menüsünden **Çıkış** seçilirse hem kayıt tamamen durur hem de buffer klasörü temizlenir.

## Özellikler

- Donanım hızlandırmalı kayıt (NVENC / QSV / AMF) + otomatik algılama ve yazılım fallback
- Ayarlanabilir buffer süresi (1–30 dk) ve FPS (24 / 25 / 30 / 60)
- Özelleştirilebilir global kısayol
- System tray entegrasyonu: pencereyi göster/gizle, replay'i kısayolsuz da kaydet, çıkış
- Ayarlar `%LocalAppData%\RecordeRy\settings.json` içinde saklanır, buffer segmentleri `%LocalAppData%\RecordeRy\Buffer` içinde tutulur

## Gereksinimler

- Windows 10/11 (x64)
- Ekran kartı sürücüleri güncel (donanım encoder algılaması için)
- `ffmpeg.exe` ve `ffprobe.exe` — boyutları nedeniyle repoya dahil edilmedi, aşağıdaki gibi ayrıca temin edilmeli
- Geliştirme için: [.NET 9 SDK](https://dotnet.microsoft.com/download)

## Geliştirme ortamını kurma

1. Repoyu klonla.
2. [gyan.dev ffmpeg builds](https://www.gyan.dev/ffmpeg/builds/) (ya da ffmpeg.org) üzerinden Windows build indir; `ffmpeg.exe` ve `ffprobe.exe` dosyalarını proje kökünde `tools/ffmpeg/` klasörüne koy.
3. `RecordeRy.sln`'i Visual Studio ile aç ya da `dotnet build` çalıştır.

`RecordeRy.App` başlatıldığında `ffmpeg.exe`'yi çalıştığı klasörün yanındaki `ffmpeg/` klasöründe veya üst dizinlerdeki `tools/ffmpeg/` içinde arar ([FfmpegLocator.cs](RecordeRy.Core/FfmpegLocator.cs)).

## Tek dosya .exe olarak yayınlama

```
dotnet publish RecordeRy.App/RecordeRy.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

Bu komut `publish/RecordeRy.App.exe` adında tek bir çalıştırılabilir dosya üretir. Çalışması için `ffmpeg.exe` ve `ffprobe.exe`'nin exe ile aynı klasörde, `publish/ffmpeg/` altında bulunması gerekir — dağıtırken bu klasörü de birlikte taşı.

## Proje yapısı

| Proje | İçerik |
|---|---|
| `RecordeRy.App` | WPF arayüzü, system tray, global kısayol yönetimi, pencere/uygulama yaşam döngüsü |
| `RecordeRy.Core` | Ekran yakalama (Desktop Duplication), encoder seçimi, replay buffer ve segment yönetimi, ayarlar |
| `RecordeRy.CaptureTest` | Yakalama katmanını tek başına denemek için konsol projesi |
