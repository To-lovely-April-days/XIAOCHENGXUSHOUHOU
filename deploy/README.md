# 部署到云服务器（宝塔面板）

目标：`https://shouhou.sh-htlab.com` → 宝塔 nginx（HTTPS）→ 本机 `127.0.0.1:5080`（.NET 后端，systemd 守护）。
同一台服务器上的官网 `sh-htlab.com` / `www` 不受影响——这里只新增一个独立站点。

## 0. 前提

- 阿里云云解析：`sh-htlab.com` 下新增 **A 记录**，主机记录 `shouhou`，记录值为服务器公网 IP。
  验证：`dig +short shouhou.sh-htlab.com` 能返回 IP。
- 小程序后台 → 开发管理 → 开发设置 → 服务器域名：request / uploadFile / downloadFile 已填 `https://shouhou.sh-htlab.com`（已完成）。

## 1. 打包（开发机）

```bash
bash deploy/build.sh
```
生成 `dist/huotong-aftersales-linux-x64.tar.gz`（约 50 MB，自带 .NET 运行时，服务器不用装 .NET）。

## 2. 上传并安装（服务器，root）

用宝塔「文件」上传到 `/tmp`，或 `scp dist/huotong-aftersales-linux-x64.tar.gz root@服务器IP:/tmp/`，然后在宝塔「终端」执行：

```bash
tar -xzf /tmp/huotong-aftersales-linux-x64.tar.gz -C /tmp && bash /tmp/huotong-aftersales/install.sh
```

第一次运行会生成 `/etc/huotong-aftersales.env` 并停下，编辑它填写：

| 变量 | 说明 |
|---|---|
| `WeChat__AppSecret` | 小程序 AppSecret |
| `Jwt__Key` | `openssl rand -base64 48` 生成的随机串 |
| `Bootstrap__SupervisorName` / `Bootstrap__SupervisorPhone` | 第一个主管的姓名、手机号（库里没有主管时创建一次） |
| `Bootstrap__SupervisorPassword` | 电脑后台初始密码（≥8 位，含字母和数字） |

填好后再执行一次 `bash /tmp/huotong-aftersales/install.sh`，最后一行应显示 `本机自检 /admin/ → HTTP 200`。

程序在 `/www/wwwroot/huotong-aftersales`，数据库 `data/aftersales.db`、上传文件 `uploads/` 都在该目录下，**更新时会保留**。请在宝塔「计划任务」里给这个目录加每日备份。v1.2 起工单附件按原图 / 原文件保存（单个最大 100MB），`uploads/` 增长会明显变快，**请关注磁盘空间**（`df -h`、`du -sh /www/wwwroot/huotong-aftersales/uploads`）。

## 3. 宝塔建站 + HTTPS

1. 宝塔 → 网站 → 添加站点：域名 `shouhou.sh-htlab.com`，PHP 选「纯静态」，不建数据库。
2. （解析生效后）站点设置 → SSL → Let's Encrypt → 勾选 `shouhou.sh-htlab.com` → 申请；开启「强制 HTTPS」。
3. **不要用宝塔「反向代理」菜单**：宝塔站点默认带 `location ~ .*\.(js|css)?$` 这类正则规则（`?` 使它匹配所有地址），会抢在普通 `location /` 前面，导致后台的 js、上传的照片 404。
   改为新建文件 `/www/server/panel/vhost/nginx/extension/shouhou.sh-htlab.com/huotong-proxy.conf`（站点主配置已 include 该目录，宝塔改主配置也不会覆盖它）：
   ```nginx
   client_max_body_size 110m;

   location ^~ /.well-known/ {
       root /www/wwwroot/shouhou.sh-htlab.com;
       allow all;
   }

   location ^~ / {
       proxy_pass http://127.0.0.1:5080;
       proxy_http_version 1.1;
       proxy_set_header Host $host;
       proxy_set_header X-Real-IP $remote_addr;
       proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
       proxy_set_header X-Forwarded-Proto $scheme;
       proxy_read_timeout 300s;
       proxy_send_timeout 300s;
   }
   ```
   然后 `nginx -t && nginx -s reload`。

   > **从 v1.1 升级到 v1.2 必须手工改这个文件**（更新程序包不会动 nginx 配置）：把 `client_max_body_size 30m;` 改为 `client_max_body_size 110m;`，把 `proxy_read_timeout 120s;` 改为 `300s`，并新增一行 `proxy_send_timeout 300s;`，然后执行 `nginx -t && nginx -s reload`。
   > v1.2 的工单附件单个最大 100MB；不改的话，超过 30MB 的上传会被 nginx 直接返回 413，小程序和电脑后台会提示「文件不能超过 100MB」。可用 `grep -n client_max_body_size /www/server/panel/vhost/nginx/extension/shouhou.sh-htlab.com/huotong-proxy.conf` 确认已生效。

## 4. 验证

```bash
curl -I https://shouhou.sh-htlab.com/admin/
```
返回 200 后：浏览器打开 `https://shouhou.sh-htlab.com/admin/` 用初始主管登录 → 在「账号管理」添加工程师/销售（真实手机号）→ 小程序体验版里用「手机号快速验证」登录。

## 常用命令

```bash
systemctl status huotong-aftersales        # 状态
journalctl -u huotong-aftersales -n 200    # 日志
systemctl restart huotong-aftersales       # 改了 /etc/huotong-aftersales.env 之后重启
```

更新版本：开发机重新 `bash deploy/build.sh`，上传后再执行第 2 步的安装命令即可。
