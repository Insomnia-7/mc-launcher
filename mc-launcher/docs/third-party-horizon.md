# Horizon 接入第三方说明

方隅通过独立进程调用用户安装的 Horizon，未复制其引擎源码，也未将其二进制、游戏 JAR、纹理、音频或转换资源打入方隅安装包。官方构建保留完整目录和上游许可文件。

- Horizon 项目与许可：https://github.com/horizon-rust/horizon/blob/main/LICENSE
- BouncyCastle.Cryptography 2.7.0 用于官方构建 Ed25519 签名验证：https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0
- 以下保留所用 Bouncy Castle 包的完整许可证。此说明不替代对 Minecraft 游戏内容的授权。

MIT License (https://opensource.org/licenses/MIT)

Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org).
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sub license, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions: The above copyright notice and this
permission notice shall be included in all copies or substantial portions of the Software.

**THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.**
