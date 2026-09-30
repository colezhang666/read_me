# Read_me

本地项目地址：D:\read_me。后续开发、测试和发布都在此目录进行。

- src：Windows阅读器源码（.NET 10 / WinForms）
- tests：可执行的确定性检查
- app：可直接运行的软件，以及现有 ReaderData 书库
- backups：更新前的本地书库备份

打开 app\Read_me.exe 即可使用。需要 .NET 10 Desktop Runtime。
阅读数据随 ReaderData 文件夹保存，移动软件时请一同移动。书库和备份不进入 Git。

## v1.9 新增

- 设置中支持1～3倍行距，步进0.05倍；段落下方间距0～40磅。
- 在书架里右键书籍，可重命名或移入回收站。
- 在回收站里双击书籍，或右键选择恢复，恢复正文、阅读进度和书签。
- 回收站保留文件，不释放书籍占用的磁盘空间。
- 排版调节只影响显示，不改写小说正文；设置在重开软件后继续生效。

原有功能：TXT分章节导入、滚动/按屏翻阅、进度记忆、字体和留白、夜间模式、搜索、书签、托盘隐藏、专注模式、窗口记忆。
专注模式保持当前窗口大小，并隐藏标题栏和边栏。翻页模式目前仍为按屏移动。

## 开发和更新

在项目目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

脚本先运行检查，退出码非零时停止；更新前备份现有书库，再发布到固定 app 目录，不替换 ReaderData。
检查只使用测试程序自身目录中的临时 ReaderData，不操作 app 里的个人书库。

单独运行检查：

```powershell
dotnet run --project tests/PocketReader.Tests.csproj -c Release
```

可选：命令末尾追加 `-- "原小说TXT路径"`，会额外验证本次提供小说的1070章、前言及原文件副本。

Git已有迁移前基线和后续功能改动记录。历史版本文件仍在原工作目录作为迁移备份，后续不在那里改代码。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
