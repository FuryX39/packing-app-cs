# Warehouse Packing (C#)

Отдельный Windows-клиент для упаковщиков на **WPF / .NET 8**. Живёт только в этой папке и **не меняет** веб-панель, `run_web.py`, `run_api.py`, Python-клиент `warehouse_packing_app/` и любые другие модули. Ходит в уже существующие HTTP API как обычный клиент.

- менеджеры по-прежнему в веб-панели `/warehouse`;
- упаковщик запускает этот exe на ПК с принтером;
- печать PDF и ШК — локально через **Windows GDI** (растр Pdfium). SumatraPDF не используется.

Вкладки как в текущем Python-клиенте: **Задания**, **Упаковка FBS**, **Номенклатура**.

## Требования

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (для сборки)
- На сервере: `run_web.py` (обычно `:8765`) и `run_api.py` (обычно `:8766`)

## Запуск из исходников

```bat
cd warehouse_packing_app_cs
copy config.env.example config.env
dotnet run
```

В окне входа: адрес веб-панели, при необходимости адрес API, логин и пароль от `/warehouse`.

## Сборка exe

```bat
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

`config.env` кладётся рядом с `WarehousePacking.exe`.

## Печать

В настройках — принтер этикеток и А4. Размер этикетки: `paper=ШИРИНАmm x ВЫСОТАmm`, например `paper=47mm x 25mm`. Флаг `noscale` печатает страницу PDF в натуральную величину.
