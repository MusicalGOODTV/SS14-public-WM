# Weekly Mode

Weekly Mode - серверный режим для длительной кампании на одной станции между несколькими раундами. Он сохраняет не весь сервер, а bundle состояния станции: `station.yml`, metadata snapshot, overrides ролей, container patch и integrity metadata.

## Включение

Режим выключен по умолчанию.

```cfg
weekly_mode.enabled: true
weekly_mode.data_root: /weekly-mode
weekly_mode.default_autosave_minutes: 10
weekly_mode.default_retain_autosaves: 8
```

Файлы пишутся в user data, по умолчанию в `/weekly-mode/sets/<setId>/`.

## Создание set

```text
wm.set.create <setId> <baseMapPrototype> [displayName]
wm.set.list
```

`setId` принимает только ASCII-буквы, цифры, `-`, `_`, `.`. Пути, слеши и `..` запрещены.

## Роли

Отключение ролей не меняет `JobPrototype` и YAML:

```text
wm.roles.disable <setId> <jobId1> ... <jobId10>
wm.roles.enable <setId> <jobId1> ...
wm.roles.clear <setId>
```

Переименование ролей хранится как runtime alias:

```text
wm.roles.aliases <setId>
wm.roles.rename <setId> <jobId> "<alias>"
wm.roles.rename-batch <setId> JobId="Alias";JobId="Alias"
wm.roles.rename-clear <setId> [jobId...]
```

Alias поддерживает Unicode, но запрещает управляющие символы и markup brackets.

## Старт

```text
wm.start <setId> [snapshotId]
```

Если snapshot не указан, используется current snapshot set-а. Если current snapshot отсутствует, старт идет с base map.

## Сохранение

```text
wm.save <setId> "<note>"
```

Autosave запускается во время активного weekly round по интервалу set-а. Старые autosave удаляются по retention, manual snapshots не удаляются retention-логикой.

Snapshot считается успешным только когда записан полный bundle:

```text
station.yml
snapshot.json
role-overrides.json
container-patch.json
integrity.json
```

Запись идет через временную директорию snapshot и затем заменяет финальную директорию.

## Snapshots

```text
wm.snapshots <setId>
wm.snapshot.delete <setId> <snapshotId>
wm.status [setId]
```

`wm.snapshots` показывает ID, тип, UTC-время, возраст, размер, заметку, автора, совместимость build-а и current marker.

## Rollback

Без force команда создает confirmation token:

```text
wm.rollback <setId> <snapshotId>
wm.rollback <setId> <snapshotId> <token>
```

Для немедленного выполнения:

```text
wm.rollback <setId> <snapshotId> --force
```

Перед rollback создается `rollback-backup` текущего мира, затем раунд перезапускается в выбранный snapshot.

## Завершение

```text
wm.cancel
```

После отмены будущие раунды используют обычный выбор карты. Runtime role aliases автоматически перестают применяться, потому что resolver активен только во время Weekly Mode.

## Расположение файлов

```text
/weekly-mode/state.json
/weekly-mode/sets/<setId>/set.json
/weekly-mode/sets/<setId>/snapshots/<snapshotId>/station.yml
/weekly-mode/sets/<setId>/snapshots/<snapshotId>/snapshot.json
/weekly-mode/sets/<setId>/snapshots/<snapshotId>/role-overrides.json
/weekly-mode/sets/<setId>/snapshots/<snapshotId>/container-patch.json
/weekly-mode/sets/<setId>/snapshots/<snapshotId>/integrity.json
```

Для резервного копирования достаточно копировать весь каталог `/weekly-mode`.

## Ограничения persistence

Weekly Mode исключает player-controlled entities, player bodies через active session roots, minds, ghosts/observers и descendants excluded roots. Snapshot compatibility между обновлениями не гарантируется: несовместимые `schemaVersion`, engine или content build нужно проверять перед rollback.

Контейнерный patch применяется best-effort после загрузки карты: поврежденные или отсутствующие entity/container записи логируются и пропускаются.
