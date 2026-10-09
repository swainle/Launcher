# Launcher

可执行文件旁的 `Config/launcher.json` 用于启动页，配置 `releaseEndpoint`；`Config/publish.json` 用于发布页。对应文件存在时显示相应标签页，两个文件可独立使用。

发布功能位于“版本发布”标签页。先编辑可执行文件旁的 `Config/publish.json`，配置 `releasesDirectory`、发布地址及 COS 参数。`indexTitle` 设置索引页的浏览器标题和页面标题。默认扫描可执行文件所在目录；如需覆盖，可设置 `sourceDirectory` 或在 `platformSources` 中按 `windows/amd64` 指定平台源目录。目录可以写成相对可执行文件的路径。`excludePaths` 列出相对扫描目录的文件或目录，默认忽略 `Config`、`Data`、`Logs`、`runtimes`、`Releases` 和下载缓存 `release`；目录项会连同子目录一起排除。`Config` 和扫描目录内的本地 `Releases` 始终排除。配置文件在构建和发布时按“较新则复制”处理。

界面按版本、平台、变更文件三列操作。点击版本和平台行即可选中，背景色标出当前选择。选定版本和平台后扫描差异，勾选文件并点击“新建更新包”。未勾选的变更不会进入包，也不会改变该版本的文件索引。之后点击“发布更新包”。

新建版本输入框默认建议最高数字版本的末段加 1。版本和平台下方的删除按钮会列出当前选中的未发布项供确认；已发布项及仍被后续更新包依赖的项不能删除。删除只影响本地 `Releases`，不会删除 COS 对象。

每个版本使用以下目录结构：

```text
Releases/
  index.json
  index.html
  v0.1.3/
    manifest.json
    upgrade/windows/amd64/files/{path}
```

`manifest.json` 的 `inventory` 保存本版全部文件的路径、大小及 SHA-256；更新包的 `files` 只保存变更文件，`deleted` 记录删除路径。上一版本由版本顺序确定。`index.json` 保存发布索引，`index.html` 展示当前稳定版；历史版本和变更清单可选显示。发布时只上传选定平台的更新包，并合并远端同版本清单，最后更新 `channels/stable.json`。下载更新时，本地版本必须与索引中的上一版本一致。

代码按职责划分为 `VersionService`（版本与清单）、`PackageService`（扫描与打包）、`ReleaseIndexService`（本地索引和页面）、`PublishService`（发布到 COS）和 `UpdateService`（检查与下载更新）。打包完成后，界面调用索引服务生成发布页。

构建：`dotnet build Launcher/Launcher.csproj`。发布流程自检：`dotnet run --project UpdaterTests/UpdaterTests.csproj`。
