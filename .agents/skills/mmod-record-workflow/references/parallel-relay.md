# Parallel Relay 编排规范

仅当用户明确要求并行时读取。

## 准入

必须同时满足：

1. 至少两个任务无直接或传递依赖，可同时开始。
2. 每个任务有精确且互斥的写集；解决方案、公共类型、项目文件、共享样式、资源字典、测试基础设施和文档索引都视为潜在冲突。
3. 公共 API、数据模型、状态机、持久化格式和构建配置由唯一 owner 在 fan-out 前冻结。
4. 每个节点可独立 Review、运行最小验证并给出结构化回执。
5. 唯一协调者负责 join、全局 diff、集成修复、最终验证和获授权的 Git 操作。

任一条件不成立即使用 Standard Relay。

## 交付物与 DAG

必须一次性交付：总控计划、每个节点的小计划、协调者提示词、各节点完整提示词、join/closeout 提示词和统一回执格式。节点声明 id、owner、needs、reads、writes、produces、verification，状态只用 pending、ready、running、succeeded、blocked、failed。

只有 ready 节点可启动。fan-out 时在等待前连续启动至少两个互斥 worker，并记录 agent id、started_at、finished_at 与状态快照；没有时间重叠证据不得称为并行。协调者默认亲自处理 foundation、join 和 closeout。

## 共享工作区

一个文件同一时刻只有一个写 owner。worker 不修改共享冻结物、总控计划、其他节点计划或 Git index，不 commit/push/stash/reset/clean。发现公共缺口时提交 change request，由协调者在 join 串行处理。

## Worker 回执

```text
node: <id>
status: succeeded | blocked | failed
completed_items: <items>
changed_files: <files>
verification: <commands and results>
contract_requests: <none or request>
residuals: <remaining>
first_error: <none or error>
next_owner_action: <action>
```

## Join

协调者等待所有已启动节点终止后，核对计划、status、diff、写集和真实验证；处理越权、交叉写入、冻结漂移与 change request；执行独立 Review、修复与风险匹配验证。仅在全局验收成立并获得授权后统一 stage/commit/push。

