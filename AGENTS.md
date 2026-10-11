# ParseEngine —— 代码编写要求与提交要求

本文件只写**通用的代码编写要求与提交要求**；具体功能的设计与实现进度以代码、文档和
回归测试为准。

硬性要求（本仓库所有"用解析器生成器做库/工具"的通用要求）：
1. **必须写完整的文法文件**：`.gram` 是这门语言的**权威语法定义**，产生式要覆盖
  完整语法结构（文档 / 块 / 语句 / 参数 / 列表 / 嵌套），关键字用
   `%token NAME "字面量"` **逐个登记**，标点/字符串/数字/标识符各走
   `%token` / `%token regex` / `%identifier`，注释走 `%trivia`，空白走 `%space`；
   语义动作（`$$` / `$n`）只调用 `%code` 里的语义层。
2. **`.gram` 不能当"词法的提取器"**：禁止用一个大 `KW` / 一个万能 Token / 一条
   "任意 token 流"的产生式把整门语言糊过去，也禁止把每个关键字只当成待提取的词素
   堆进正则——那种写法等于没有文法，评审一律退回。关键字、块、命令都要能在产生式
   里被点名与组合。
3. **解析流程放进解析器生成器**：块栈、逐行折叠、驱动 `Parser::Engine::runText`
   的流程用 `%code` / `%epilogue include` 内嵌进生成产物，`src/` 里不再手写解析驱动。
4. **引用仓库三个库**（各自拷一份进 `metro-lang/`，跟 Magel 的做法一致）：
   `library/preprocess`（`%IF`/`%INCLUDE`/`%IMPORT`，用 `forMetro()`）、
   `library/template`（反编译**模板化生成**，模板放 `metro-lang/template/templates/*.tmpl`）、
   `library/json`（模型 ↔ JSON，对齐《Json格式参考文档》）。
5. **固化模板代码**：`template/templates/` 的模板在构建期**内嵌成 C++ 源**（生成
  `*_templates.cpp/.hpp`），运行时不读磁盘模板，保证单文件可用。
6. **完整文档注释**：头文件与 `.cpp` 的公开类型/函数都要中文 doc comment
  （`@brief` / `@param` / `@return`），沿用仓库风格。
7. 反编译用模板化生成；区间/等待/总时间按《列车运行时间计算》的模型。
8. 根 `doc/README.md` 挂索引；最后 `git add -A` → commit → `git push origin master`。

## Git 提交信息（全仓库强制约束）

Git 提交身份按以下条件强制规范，适用于新建、补写、amend 及历史重写提交：

- 分别检查作者（author）与提交者（committer）的身份名称。名称同时包含“小兔”和“糖糖”时，该身份的名称必须统一为 **`小兔糖糖OvO`**，邮箱必须同时统一为 **`rabbitc999@outlook.com`**。
- 不满足上述条件的身份保留其真实名称和邮箱，不得批量覆盖其他贡献者或平台提交者的身份。这里的“名称”指 Git 作者／提交者名称；提交标题仍遵守下方的中文要求。
- 当前工作身份满足上述条件，仓库本地配置使用 `git config --local user.name "小兔糖糖OvO"` 和 `git config --local user.email "rabbitc999@outlook.com"`。提交前用 `git var GIT_AUTHOR_IDENT`、`git var GIT_COMMITTER_IDENT` 核对实际身份，避免环境变量或其他配置导致名称、邮箱不符合要求。

本仓库所有 Git 提交的标题及正文（如有）**必须使用中文**，禁止使用纯英文提交说明。文件名、命令、API 标识符和必要的技术术语可以保留原文，但改动说明必须用中文表达。此约束适用于本仓库的所有修改，不限于使用解析器生成器的库或工具。

