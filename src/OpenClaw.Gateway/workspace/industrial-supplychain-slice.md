# 工业本体在供应链的切片

## 1. 切片请求

- **任务名称**：工业本体供应链切片提取
- **切片主题**：供应链核心概念与关系
- **任务目标**：提取工业 ontology 中与供应链直接相关的核心概念、关系与约束，用于供应链系统建模与互操作性保障
- **期望输出**：概念表 + 关系表 + 约束规则列表

## 2. 切片范围

### 纳入范围
- 供应链核心实体（Supplier, Manufacturer, Distributor, Retailer 等）
- 物流服务实体（Carrier, FreightForwarder, LogisticsServiceProvider）
- 物料与产品实体（MaterialProduct, Inventory, Cargo, Shipment）
- 流程实体（TransportProcess, StorageProcess, LogisticsProcess）
- 供应链关系（SupplyRelationship, SupplyChainSystem）
- 可追溯性实体（TraceableResourceUnit, TrackingEvent）

### 排除范围
- 财务与支付相关概念（不纳入本次切片）
- 营销与销售预测概念（不纳入本次切片）
- 人力资源管理概念（不纳入本次切片）

## 3. 依据来源

| 来源 | 用途 | 信任度 |
|------|------|--------|
| IOFoundry SCRO (v1 Beta 2022-11-18) | 供应链核心类与属性定义 | **高** |
| GS1 标准 | 标识与编码方案 | **高** |
| APICS 行业标准 | 供应链术语定义 | **高** |

**来源引用**：https://github.com/iofoundry/ontology/tree/master/supplychain

---

## 4. 切片摘要

- **切片主题**：工业供应链核心域
- **切片结论一句话总结**：该切片聚焦供应链全链路的核心参与者、物料流转过程及可追溯性关系，覆盖从原材料到最终用户的完整供应链网络。
- **选取依据**：与供应链系统建模、物流调度、供应商管理直接相关
- **排除依据**：财务、营销、人力资源等非核心供应链概念不纳入

---

## 5. 核心概念

| 概念ID | 中文名称 | 英文名/标识符 | 类型/层级 | 定义 | 关键属性 | 上位概念 | 别名/同义词 |
|-------|---------|--------------|----------|------|---------|---------|-----------|
| C1 | 供应商 | Supplier | 实体 | 向其他供应链参与者提供材料产品或商业服务的个人或组织 | 供应商ID、名称、能力评级 | SupplyChainAgent | 供方 |
| C2 | 制造商 | Manufacturer | 实体 | 生产产品或提供制造服务的个人或组织 | 制造商ID、生产能力、生产类型 | SupplyChainAgent | 厂商 |
| C3 | 分销商 | Distributor | 实体 | 从制造商采购并向批发商转售的组织 | 分销商ID、覆盖区域 | SupplyChainAgent | 批发商 |
| C4 | 批发商 | Wholesaler | 实体 | 从分销商采购并向零售商转售的组织 | 批发商ID、渠道类型 | SupplyChainAgent | 分销商 |
| C5 | 零售商 | Retailer | 实体 | 向最终用户销售产品的个人或组织 | 零售商ID、门店类型 | SupplyChainAgent | 零售商 |
| C6 | 承运人 | Carrier | 实体 | 提供运输服务的个人或组织 | 承运人ID、运输方式 | LogisticsServiceProvider | 运输商 |
| C7 | 货运代理人 | FreightForwarder | 实体 | 代表发货人或收货人安排运输的组织和个人 | 货代ID、服务范围 | LogisticsServiceProvider | 货代 |
| C8 | 库存 | Inventory | 实体 | 代理商持有以满足外部或内部需求的物料实体 | 库存ID、数量、位置 | MaterialEntity | 存货 |
| C9 | 工业库存 | IndustrialInventory | 实体 | 准备销售或使用的工业物料，存储于仓储或工厂车间 | 库存ID、生产批次、用途 | Inventory | 在制品/成品库存 |
| C10 | 货物 | Cargo | 实体 | 通过运输设备运输的物料实体 | 货物ID、重量、体积 | MaterialEntity | 货运 |
| C11 | 运输设备 | TransportEquipment | 实体 | 用于装载、保护和固定物料的设备 | 设备ID、类型、容量 | Equipment | 运输载具 |
| C12 | 集装箱 | Container | 实体 | 设计用于容纳物料实体的运输设备 | 集装箱ID、尺寸类型 | TransportEquipment | 货柜 |
| C13 | 货运单元 | LogisticUnit | 实体 | 为运输或仓储打包在一起的物料实体集合 | 物流单元ID、追踪码 | TraceableResourceUnit | 物流单元 |
| C14 | 可追溯资源单元 | TraceableResourceUnit | 实体 | 唯一标识的物料实体，需要可追溯其历史、应用或位置 | TRU ID、批次号 | MaterialEntity | 可追溯单元 |
| C15 | 货运 | Shipment | 实体 | 交付至收货人位置且经历相同发运和收货过程的物料实体集合 | 货运ID、起止点、状态 | LogisticUnit | 发货/ shipment |
| C16 | 供应链节点 | SupplyChainNode | 实体 | 作为供应链流程场所的地理位置 | 节点ID、类型、坐标 | GeospatialSite | 节点 |
| C17 | 供应链系统 | SupplyChainSystem | 实体 | 由参与者、设施、机器和信息系统组成的设计交付产品和服务的工程系统 | 系统ID、参与者列表 | EngineeredSystem | 供应链网络 |
| C18 | 供应关系 | SupplyRelationship | 实体 | 两个代理商之间存在的一方为另一方供应产品或服务的关系 | 关系ID、协议类型、期限 | RelationalEntity | 供求关系 |
| C19 | 生产批次 | Lot | 实体 | 一起生产并共享相同生产历史和规格的物料实体数量 | 批次ID、生产日期 | TraceableResourceUnit | 批次 |
| C20 | 追踪事件 | TrackingEvent | 事件 | 至少有一个可追溯资源单元或设备参与的事件 | 事件ID、时间、地点 | Event | 物流事件 |
| C21 | 仓储设施 | StorageFacility | 实体 | 设计用于存储材料或商品的设施 | 设施ID、容量、类型 | Facility | 仓库 |
| C22 | 工厂 | Factory | 实体 | 设计用于生产产品并包含一个或多个生产系统的设施 | 工厂ID、生产线 | Facility | 制造工厂 |

---

## 6. 核心关系

| 关系ID | 主体 | 关系 | 客体 | 关系说明 | 基数/方向 | 适用条件 | 来源 |
|--------|-----|------|------|---------|----------|----------|------|
| R1 | 供应商 | suppliesToAtSomeTime | 客户 | 供应商向客户销售产品或服务 | n:n | 供应关系存续期间 | SCRO Properties |
| R2 | 供应关系 | dependsOnSupplier | 供应商 | 供应关系连接供应商与客户 | n:1 | 关系建立时 | SCRO Properties |
| R3 | 供应关系 | dependsOnBuyer | 客户 | 供应关系连接客户与供应商 | n:1 | 关系建立时 | SCRO Properties |
| R4 | 供应关系 | dependsOnProduct | 产品 | 供应关系连接产品与供应商和客户 | n:1 | 关系建立时 | SCRO Properties |
| R5 | 运输过程 | occursAt | 供应链节点 | 运输过程在供应链节点发生 | n:n | 物流执行期间 | SCRO Properties |
| R6 | 供应链系统 | contains | 供应链节点 | 供应链系统包含若干供应链节点 | 1:n | 系统定义时 | SCRO Classes |
| R7 | 供应链系统 | hasParticipant | 供应链参与者 | 供应链系统由参与者实现 | 1:n | 流程执行时 | SCRO Classes |
| R8 | 货运 | startsAt | 发货地点 | 货运从发货地点开始 | n:1 | 发运流程启动 | SCRO Properties |
| R9 | 货运 | endsAt | 收货地点 | 货运在收货地点结束 | n:1 | 收货流程完成 | SCRO Properties |
| R10 | 可追溯单元 | hasTrackingEvent | 追踪事件 | 可追溯单元在供应链中产生追踪事件 | 1:n | 物流节点过境 | SCRO Classes |
| R11 | 制造商 | produces | 产品 | 制造商生产产品 | n:n | 生产执行期间 | SCRO Classes |
| R12 | 库存 | ownedBy | 组织 | 库存由组织拥有 | n:1 | 库存管理期间 | SCRO Properties |
| R13 | 货物 | loadedOn | 运输设备 | 货物装载于运输设备 | n:1 | 运输开始前 | SCRO Classes |

---

## 7. 约束与规则

| 约束ID | 约束名称 | 适用对象 | 规则内容 | 触发条件 | 禁止项 | 来源 |
|--------|---------|----------|---------|----------|--------|------|
| K1 | 供应链闭环 | SupplyChainSystem | 供应链系统必须包含至少一个供应商和一个客户 | 系统初始化 | 无供应商或无客户 | SCRO 设计原则 |
| K2 | 货运状态追溯 | Shipment | 每个货运必须能够追溯其从发货到收货的完整状态 | 物流执行期间 | 货运状态不可追溯 | 供应链追溯要求 |
| K3 | 产品-供应商关联 | SupplyRelationship | 每个供应关系必须关联一个特定产品 | 关系建立 | 无产品供应关系 | SCRO 关系模型 |
| K4 | 位置必填 | SupplyChainNode | 每个供应链节点必须有有效的地理位置坐标 | 节点创建 | 无效坐标 | 物流调度要求 |
| K5 | 批次追溯 | Lot | 每个批次必须有唯一的批号用于追溯 | 生产完成 | 批号重复或缺失 | 质量追溯要求 |
| K6 | 运输设备容量 | TransportEquipment | 运输设备不能超过其额定容量装载 | 装载过程 | 超载 | 运输安全规范 |

---

## 8. 术语映射与歧义

| 术语 | 可能映射对象 | 当前采用定义 | 不采用其他定义的原因 |
|------|------------|-------------|------------------|
| 供应商 | Supplier / Vendor | SCRO 定义为：向其他供应链参与者提供产品或服务的代理商 | "Vendor"在电商场景有细微差别 |
| 分销商 | Distributor / Wholesaler | Distributor = 从制造商采购转售给批发商<br>Wholesaler = 从分销商采购转售给零售商 | 两者处于供应链不同层级 |
| 物流单元 | LogisticUnit / Shipment | LogisticUnit = 为运输/仓储打包的物料集合<br>Shipment = 经历相同发运/收货过程的物料集合 | Shipment 强调过程，LogisticUnit 强调包装形态 |
| 供应链节点 | SupplyChainNode / Location | SupplyChainNode = 供应链流程发生的场所 | 突出供应链属性区别于普通位置 |

---

## 9. 不确定项

- **待补充**：本切片目前基于 SCRO v1 Beta (2022-11)，正式版本可能存在更新
- **待确认**：与财务相关的支付和结算关系未纳入，是否需要扩展视业务需求而定

---

## 10. 后续动作建议

1. **扩展财务域**：如业务需要，可扩展支付、发票、结算等相关概念
2. **JSON Schema 生成**：基于本切片概念表生成对应的 JSON Schema 用于数据交换
3. **OWL 本体映射**：将核心概念映射到 OWL Class，支持语义推理
4. **领域细化**：可进一步细化为"制造业供应链"、"农业供应链"等子领域切片

---

## 附录：核心类层次结构

```
SupplyChainSystem
├── SupplyChainNode (地理位置)
│   ├── Factory (工厂)
│   ├── StorageFacility (仓储设施)
│   ├── DistributionCenter (配送中心)
├── SupplyChainAgent (供应链参与者)
│   ├── Manufacturer (制造商)
│   ├── Supplier (供应商)
│   ├── Distributor (分销商)
│   ├── Wholesaler (批发商)
│   ├── Retailer (零售商)
│   ├── LogisticsServiceProvider (物流服务提供商)
│       ├── Carrier (承运人)
│       ├── FreightForwarder (货运代理人)
├── MaterialEntity (物料实体)
│   ├── Inventory (库存)
│   │   └── IndustrialInventory (工业库存)
│   ├── Cargo (货物)
│   ├── TraceableResourceUnit (可追溯资源单元)
│       ├── LogisticUnit (物流单元)
│       ├── Lot (批次)
│       └── Shipment (货运)
│   └── TransportEquipment (运输设备)
│       ├── Container (集装箱)
│       └── Trailer (拖车)
├── SupplyRelationship (供应关系)
├── SupplyChainProcess (供应链流程)
│   ├── TransportProcess (运输过程)
│   ├── StorageProcess (仓储过程)
│   └── LogisticsProcess (物流过程)
└── TrackingEvent (追踪事件)
```

---

**切片版本**：v1.0  
**生成日期**：2026-04-21  
**数据来源**：IOFoundry Supply Chain Reference Ontology (SCRO)