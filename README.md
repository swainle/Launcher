# Launcher

Launcher 是桌面应用程序。它检查和下载更新，也构建和发布更新包。发布目标是腾讯云对象存储（COS）。

## 构建

安装 .NET 10 SDK。然后运行：

```sh
dotnet build Launcher/Launcher.csproj
```

运行发布流程自检：

```sh
dotnet run --project UpdaterTests/UpdaterTests.csproj
```

## 配置页面

将配置文件放在可执行文件旁的 `Config` 目录中。

- 如需显示启动页面，添加 `Config/launcher.json`。将 `releaseEndpoint` 设为发布目录的 HTTPS 地址。
- 如需显示发布页面，添加 `Config/publish.json`。设置 `releaseEndpoint`、`cosBucketURL`、`secretID` 和 `secretKey`。如果 COS 凭据需要临时令牌，也设置 `sessionToken`。

两个页面分别读取自己的配置文件。构建时，如果源配置文件较新，构建工具就将其复制到输出目录。

在发布配置中，`releasesDirectory` 指定本地输出目录。默认值是 `Releases`。`sourceDirectory` 指定打包文件所在的目录。默认值是可执行文件所在的目录。如需为某个平台指定不同的源目录，在 `platformSources` 中添加条目。键的格式如 `windows/amd64`。相对路径以可执行文件所在的目录为起点。

`excludePaths` 列出扫描时要排除的路径。默认值是 `Releases`、`release`、`Config`、`Data`、`Logs` 和 `runtimes`。排除一个目录时，也排除其中的文件。扫描始终排除 `Config` 和本地 `Releases` 目录。`indexTitle` 指定发布页标题。`showHistory` 控制发布页是否显示历史版本。

## 下载更新

1. 打开启动页面。
2. 单击 **Check Latest Version**，查看最新稳定版本。
3. 单击 **Download Update**，下载并校验更新。

Launcher 将下载的文件放在可执行文件旁的 `release/current/<os>/<arch>` 目录中。只有本地版本是发布索引中的上一版本时，才能应用更新。

## 构建并发布版本

1. 打开发布页面。输入版本号，例如 `v0.1.3`。单击版本栏中的 **New**。
2. 选择操作系统和架构。单击平台栏中的 **New**。
3. 选择版本和平台。Launcher 将扫描源目录。源文件变化后，单击 **Rescan Changes**。
4. 勾选要放入升级包的变更文件。不需要的变更保持未勾选。
5. 勾选 **Upgrade package**、**Full package**，或同时勾选两项。升级包包含勾选的变更。完整包包含源文件。
6. 单击 **Build**。Launcher 将生成 ZIP 文件和本地发布页。
7. 单击 **Preview**，检查发布页。然后单击 **Publish**，上传选定的包。

构建升级包时，至少要勾选一项变更。构建完整包时，不使用变更文件的勾选结果。发布升级包会更新 `channels/stable.json`。只发布完整包不会改变稳定版频道。

如需删除草稿版本或平台，先选择该项，再单击 **Delete**。应用程序会要求确认。已发布的项目不能删除。后续更新依赖的项目也不能删除。删除操作只影响本地 `Releases` 目录，不会删除 COS 对象。

## 发布文件

Launcher 按以下结构保存发布文件：

```text
Releases/
  index.json
  index.html
  v0.1.3/
    manifest.json
    upgrade/windows/amd64/files/{path}
    full/windows/amd64/files/{path}
    archives/windows/amd64/upgrade.zip
    archives/windows/amd64/full.zip
```

`manifest.json` 记录一个版本的文件。`inventory` 记录每个文件的路径、大小和 SHA-256 值。升级包中的 `files` 记录变更文件。`deleted` 记录删除的路径。`index.json` 记录发布索引。`index.html` 是发布页。
