# Proxy Manager

Пускает выбранные программы Windows через SOCKS5/HTTP-прокси. Остальные работают напрямую.

## Установка

Скачайте установщик из [Releases](https://github.com/Morhuhan/proxy-manager/releases/latest). Нужна Windows 10/11 x64.

## Использование

1. **Прокси** — добавьте прокси или вставьте строку вида `socks5://user:pass@host:port`.
2. **Программы** — выберите программу и назначьте ей прокси.
3. Нажмите **«Запустить»**.

## Сборка

```powershell
.\scripts\fetch-singbox.ps1
dotnet run --project src\ProxyManager
```

Релиз (установщик и zip в `dist\`): `.\scripts\build-release.ps1 -Version X.Y.Z`

---

Работает на ядре [sing-box](https://github.com/SagerNet/sing-box) (GPL-3.0), лицензии — в [licenses/](licenses/).
