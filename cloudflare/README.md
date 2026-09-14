# 公告推送 · 部署与控制台使用（Cloudflare 免费）

一次部署，之后只在控制台发消息，**不需要更新应用**。

## 一、一次性部署（约 3 分钟）

### 方式 A：网页操作（不用装工具）

1. 打开 Cloudflare Dashboard → **Workers & Pages → KV → Create namespace**，名字填 `blt_announce`。
2. → **Create → Worker**（名字随意，例如 `blt-announce`）→ **Edit code**，把 `announce-worker.mjs` 的内容整段粘贴进去 → **Deploy**。
3. 在该 Worker 的 **Settings → Variables and Secrets**：
   - 添加 **KV Namespace Binding**：变量名 `ANN` → 选 `blt_announce`；
   - 添加 **Secret**：名字 `ADMIN_TOKEN`，值填一段你自己生成的长随机串（32 位以上，例如用密码管理器生成）。**这个就是控制台登录密钥，别弄丢**。
4. （可选但推荐）**Settings → Triggers → Custom Domains** 绑定 `announce.ai-daynews.xyz`（需要域名已托管在 Cloudflare）。不绑也能用，用 `https://blt-announce.<你的子域>.workers.dev` 即可。
5. 浏览器打开 `https://<你的域名>/admin` → 粘贴 `ADMIN_TOKEN` → 登录。

### 方式 B：命令行（可选）

```toml
# wrangler.toml
name = "blt-announce"
main = "announce-worker.mjs"
compatibility_date = "2025-01-01"
[[kv_namespaces]]
binding = "ANN"
id = "<创建命名空间后拿到的 id>"
```

```bash
wrangler kv namespace create blt_announce      # 记下返回的 id
wrangler secret put ADMIN_TOKEN                # 粘贴你的长随机串
wrangler deploy
```

## 二、客户端指向这个 Worker

应用默认地址是 `https://announce.ai-daynews.xyz/announcements`。若你用的是 `workers.dev` 默认域，改 `data/config.json`：

```json
{
  "announceUrl": "https://blt-announce.<你的子域>.workers.dev/announcements",
  "announceFallbackUrl": "",
  "announceEnabled": true
}
```

`announceFallbackUrl` 可选（主地址失败时的第二个源，例如 GitHub raw 上的同一份 JSON）。

## 三、日常发公告（控制台）

打开 `https://<你的域名>/admin`：

1. **＋ 新建公告** → 选类型：
   - **普通通知** → 出现在应用**右下角卡片**，用户点「知道了」后不再出现；
   - **持续通知** → 常驻在**顶部横幅**，直到过期或你下架；
   - **强通知** → **全屏模态**，必须点「我已阅读并确认」（`soft` 可「稍后」，下次启动再提醒；`hard` 不确认不能用）。
2. 填标题、正文；可加 1 个按钮（「立即更新」直接调应用内更新，或「打开链接」）。
3. 需要时设置：级别（info/warn/critical）、定时发布、过期时间（到点自动下架 = 撤回）、通道（maui/electron）、版本区间、**指定 UID**（留空=所有用户）。
4. 点 **发布**。客户端 **5 分钟内**看到（打开「紧急模式」后 **60 秒内**）。

其它控制台功能：**保存草稿**、**从线上删除此条**、**发布历史 + 一键恢复上一版**（防误操作）、**回执统计**（每条显示"已读 / 已确认"人数）、**公告开关**（一键关闭所有公告）。

## 四、接口速查

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/announcements?uid=&v=&ch=maui` | 客户端拉取（服务端已按时间/版本/通道/UID 过滤，带 ETag） |
| POST | `/receipt` | 客户端已读/确认回执（只存 uid 哈希与计数） |
| GET | `/admin` | 控制台页面 |
| GET | `/admin/api/state` | 管理：线上内容 / 草稿 / 历史 / 统计 |
| POST | `/admin/api/publish` `/draft` `/rollback` `/cfg` | 发布 / 草稿 / 回滚 / 全局设置 |

## 五、本地自测（不部署也能验）

把应用 `data/config.json` 的 `announceUrl` 临时指向本地文件（放进 `wwwroot/` 即可，Kestrel 会伺服）：

```json
{ "announceUrl": "http://127.0.0.1:7460/announce-sample.json" }
```

`announce-sample.json` 形如：

```json
{ "schema": 1, "serverTime": 0, "pollAfterSeconds": 300,
  "items": [ { "id": "t1", "type": "sticky", "level": "info", "title": "测试", "body": "内容" } ] }
```

## 六、Worker 逻辑自测

```bash
node cloudflare/test-local.mjs     # 33 项断言：鉴权/过滤/ETag/回执去重/紧急模式/回滚/控制台页面
```

## 七、注意事项

- `ADMIN_TOKEN` 只在 Worker 的 Secret 里与你的浏览器本地，别写进代码或公告内容。
- 公开接口只返回"当前该看到的"条目，草稿/历史/统计不会外泄。
- 正文按纯文本渲染，按钮链接只接受 `https://`。
- 「紧急模式」记得用完关掉（它把客户端轮询从 5 分钟改成 60 秒）。
- 免费额度：KV 读 10 万/天、写 1000/天；正常使用远低于此（ETag 让大多数请求是 304）。

---

## 八、部署现状（2026-09-14 实测）

| 事实 | 结论 |
|---|---|
| `blt-announce.2579984161.workers.dev` 已部署 | 但 **`*.workers.dev` 在国内网络被完全封锁**（连已有的 bili-verify 也连不上），客户端无法使用 |
| `*.pages.dev` 可达 | 已有 Pages 站点返回 200 ✓，Pages 是备选宿主 |
| `ai-daynews.xyz` 的 NS 在阿里云，**不在这个 Cloudflare 账户里** | Worker「添加域名」报"没有区域匹配…请先将该域名添加到 Cloudflare" → **要挂自定义域需要先把这个域名的 NS 改到 Cloudflare** |

### 因此当前生效的发布路径：仓库内公告文件（今天就能用）

公告内容放在仓库的 **`announce/announcements.json`**，客户端默认地址是它的 raw URL，并且**经镜像列表拉取**（ghfast.top / gh-proxy.com / ghproxy.net，实测 200 且速度远好于直连）。

**发公告 = 打开这个文件 → 改 JSON → 提交**（GitHub 网页编辑器即可，手机也行）：

```
https://github.com/luoyunxiaotian/blt-live-tool/edit/main/announce/announcements.json
```

- 新增一条公告：往 `items` 数组里加一个对象（字段见 §三，`id` 必须唯一）。
- 撤回：删掉该条，或把 `expireAt` 改成过去时间。
- 想让客户端更快轮询：把顶层 `pollAfterSeconds` 改成 `60`（紧急公告用，记得改回 300）。
- 提交后客户端 **60 秒 ~ 5 分钟**内生效，**不需要发新版应用**。

### 何时切到控制台（可选，更舒服）

若你愿意把 `ai-daynews.xyz` 的 NS 改到 Cloudflare（阿里云控制台 → 修改 DNS 服务器 → 填 Cloudflare 给的两个 NS，并确认原有解析记录都已在 Cloudflare 里重建，避免白名单服务中断），那么 Worker 的**控制台**（`/admin`：可视化编辑、UID 定向、定时发布、历史回滚、回执统计）就能通过 `https://<你绑的域名>/admin` 使用；届时把客户端 `announceUrl` 改成 Worker 地址、`announceFallbackUrl` 保留仓库 JSON 做兜底即可。

---

## 九、上线现状（2026-09-14 完成，可直接使用）

**控制台地址（国内可达，无需改 DNS）**：https://blt-announce.pages.dev/admin
**客户端接口**：https://blt-announce.pages.dev/announcements
**登录密钥**：`ADMIN_TOKEN`（部署时设置，见下）

| 组件 | 状态 |
|---|---|
| Pages 项目 `blt-announce`（`_worker.js`） | ✅ 已部署，`/admin` 200 |
| KV 绑定 `ANN` → `blt_announce` | ✅ 已配置并在部署中生效 |
| 环境变量 `ADMIN_TOKEN` | ✅ 已配置（控制台登录用；当前值由部署者掌握，建议改存在密码管理器） |
| `GET /announcements` | ✅ 200，带 uid/版本/通道/时间过滤 + ETag 304 |
| `POST /receipt`（回执） | ✅ 可用（PoW 计数，存 uid 哈希） |
| `ai-daynews.xyz` 日报网站 | ✅ 未受任何影响（**没有改动任何 DNS 记录**） |

**注意事项**
- `*.workers.dev` 在国内被封锁，所以用的是 `*.pages.dev`（实测可达）。
- 从脚本/命令行调 `/admin/api/*` 时，Cloudflare 的 Bot 防护会拦掉默认 UA（返回 403）：带上浏览器 UA 即可（控制台网页本身不受影响）。
- 可选（更好看，但不是必需）：给 Pages 项目加自定义域 `announce.ai-daynews.xyz`，再在阿里云**新增一条** `announce` CNAME → `blt-announce.pages.dev`；不加就一直用 `pages.dev` 地址。
