# Pull Request 模板

## 这个 PR 做了什么？

<!-- 一句话说明 -->

## 关联 Issue

Closes #

## 改动类型

- [ ] 新功能
- [ ] Bug 修复
- [ ] 文档
- [ ] 重构

## 自测清单（提 PR 前必须全部打勾）

- [ ] `dotnet build src/BankingAgent.slnx -c Release` 通过（0 警告 0 错误）
- [ ] `dotnet test src/UnitTests/UnitTests.csproj` 通过
- [ ] 涉及插件改动时，`PluginValidator` 校验通过
- [ ] 没有提交 `.db`、构建产物（bin/obj）、密钥等文件
- [ ] 没有直接改动 `BankingAgent.Plugin.Sdk/` 契约（如需改动，已在群里说明并更新版本号）
- [ ] 我负责的场景，PPT 对应页面已同步更新

## 截图 / 录屏

<!-- 前端改动请附截图，方便 review -->

## 需要特别注意的地方

<!-- 有没有影响别人的接口改动？有没有遗留问题？ -->
