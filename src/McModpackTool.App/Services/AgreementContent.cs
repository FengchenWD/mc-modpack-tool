namespace McModpackTool.App.Services;

public static class AgreementContent
{
    public const string Version = "2026-08-05-v3";
    public const string LicenseUrl = "https://creativecommons.org/licenses/by-nc-sa/4.0/";

    public static string Get(string language) => language switch
    {
        "zh_HK" => TraditionalChinese,
        "en_US" => English,
        _ => SimplifiedChinese
    };

    private const string SimplifiedChinese = """
《MC整合包工具用户协议与使用须知》

生效日期：2026 年 8 月 5 日

在使用本软件前，请完整阅读并理解本协议。点击“我已阅读并同意”即表示你同意受本协议约束；如不同意，请退出并停止使用本软件。

一、软件与作者

1. 本软件名称为“MC整合包工具”，作者为 Bilibili UP 主“风尘WD”。
2. 本软件在设计、代码起草、检查、调试及文字整理过程中使用了 AI 工具辅助，并非全部由作者逐行人工编写。AI 辅助可能产生遗漏或错误，请结合兼容性报告、游戏日志及实际启动结果独立判断。
3. AI 工具及其服务提供方不是本软件的作者、维护者或担保方，也不对本软件的运行结果承担责任。
4. 本软件是围绕游戏《Minecraft》整合包处理而独立开发的第三方辅助工具，不包含、替代或授权《Minecraft》游戏本体，也并非 Minecraft 官方产品；本软件不由 Mojang Studios 或 Microsoft 开发、批准、认可、赞助或背书，本软件及作者与上述主体不存在隶属、代理或合作关系。
5. 就本软件当前设计、预期用途和分发方式而言，作者以遵守现行 Minecraft EULA 与 Usage Guidelines 为开发原则，不以修改、替代或未经授权分发游戏本体为目的。相关规则可能更新，应以官方现行文本为准；本条不构成对用户任何具体使用、修改或分发行为必然合规的保证。
   Minecraft EULA：https://www.minecraft.net/eula
   Minecraft Usage Guidelines：https://www.minecraft.net/usage-guidelines

二、许可协议（CC BY-NC-SA 4.0）

1. 本软件由作者依据“知识共享 署名—非商业性使用—相同方式共享 4.0 国际许可协议”（CC BY-NC-SA 4.0）免费许可和分发。
2. 在遵守许可条件的前提下，你可以复制、分享、转载本软件，也可以修改、改编并基于本软件创作。
3. 署名（BY）：分享或修改时，应以合理方式标注软件名称及作者“风尘WD”，提供本许可协议链接，保留已有版权与许可说明，并说明是否作出修改；不得暗示作者为你的版本、用途或行为背书。
4. 非商业性使用（NC）：不得将本软件或其修改版本主要用于获取商业利益或金钱报酬。商业授权需求应另行取得作者明确许可。
5. 相同方式共享（SA）：公开分发修改版本或演绎作品时，应继续采用 CC BY-NC-SA 4.0 或该许可允许的兼容许可。
6. 不得附加法律条款、数字版权管理措施或其他技术限制，阻止接收者行使本许可已经授予的权利。
7. 上述内容仅为主要条款摘要，不能替代许可协议法律文本。如摘要与正式文本不一致，以官方协议原文为准：
   https://creativecommons.org/licenses/by-nc-sa/4.0/

三、著作权与第三方权利

1. AI 辅助本身不当然改变作者对其具有独创性的人类创作、选择、编排、修改及整合部分享有的著作权和相关权利；具体权利范围以适用法律认定为准。
2. 在法律允许范围内，作者保留对软件功能说明、本协议未尽事项以及后续版本的解释和更新权。该约定不限制用户依法享有的权利，也不改变已经依据 CC BY-NC-SA 4.0 合法取得且依约行使的许可权利。
3. Minecraft、CurseForge、Modrinth、各加载器、模组、资源包、光影包、整合包内容、第三方库、商标及服务分别归其权利人所有，并适用各自的许可、用户协议与规则。本软件的许可不代表作者有权再次许可这些第三方内容。

四、使用条件与用户责任

1. 你应仅处理自己拥有或已获授权使用、迁移和分发的整合包及内容，并遵守适用法律、Minecraft EULA、平台规则和每个内容项目的许可条件。
2. 本软件不会授予你绕过下载限制、访问控制、平台规则或第三方许可的权利。因生成、上传、分享、运营或商业使用新整合包产生的合规责任由实施相关行为的用户承担。
3. 在迁移前应自行备份原整合包、配置、实例和世界存档。不得将本软件的静态兼容性报告视为模组一定可启动、存档一定安全或服务器一定稳定的保证。

五、联网、数据与本机文件

1. 为搜索项目、查询版本、获取加载器信息及按需下载文件，本软件会访问 CurseForge、Modrinth 及相关加载器或下载服务，并可能向这些服务发送项目 ID、文件哈希、文件名或搜索关键词、目标游戏版本和加载器等查询信息。
2. 部分核心功能需要联网。点击“我已阅读并同意”即表示你已知悉并同意本软件为实现上述功能发起必要的网络请求，并同意相关第三方服务依其规则处理请求所需信息；如不愿接受此类联网操作，请不要同意并停止使用本软件。
3. 网络中断、连接波动、DNS 或代理异常、防火墙或安全软件拦截、平台接口调整、授权变化、限流、维护或故障，以及地区网络可用性差异，均可能导致软件部分或全部功能暂时或持续无法使用、请求超时、查询或下载失败、结果不完整。作者不保证相关网络服务持续、及时或无错误可用；若介意此类风险，请勿使用本软件。
4. 本软件不会主动把你选择的整合包归档本体上传给作者。第三方服务仍可能按照其隐私政策和服务器日志规则处理你的网络地址、请求内容及其他必要连接信息。
5. 本软件会在本机创建配置记录和临时解压文件，并在正常退出时尝试清理临时内容。首次同意状态仅保存在本机配置中；删除该配置后，软件会再次显示本协议。

六、功能边界、免责声明与责任限制

1. 本软件仍在持续开发。平台元数据可能缺失、过时或错误；网络、API、下载权限、文件哈希、模组运行时行为及游戏版本差异均可能导致遗漏、误判、下载失败、启动崩溃、内容丢失或存档损坏。
2. 兼容性检查主要基于整合包清单和平台可用元数据，不执行 Minecraft 或模组代码，无法穷尽依赖版本范围、Mixin、注册表、数据包、配置、世界存档及仅在运行时出现的问题。
3. 在适用法律允许的最大范围内，本软件按“现状”和“可用状态”提供，不作适销性、特定用途适用性、无错误或不侵权等明示或默示保证。对于使用或无法使用本软件造成的间接损失、数据损失或业务中断，作者仅在法律规定的范围内承担责任；法律不得排除或限制的责任不受本条影响。

七、协议更新与其他

1. 作者可随软件功能、许可说明或合规要求更新本协议。协议发生实质更新时，后续版本可要求你重新阅读并同意；新协议不追溯剥夺已经依法取得的许可权利。
2. 本协议某一条款被认定无效或不可执行时，不影响其他条款的效力。
3. 点击同意仅表示你接受当前显示版本的协议。你可以选择不同意并退出软件。
""";

    private const string TraditionalChinese = """
《MC整合包工具用戶協議與使用須知》

生效日期：2026 年 8 月 5 日

使用本軟件前，請完整閱讀並理解本協議。按下「我已閱讀並同意」即表示你同意受本協議約束；如不同意，請退出並停止使用本軟件。

一、軟件與作者

1. 本軟件名稱為「MC整合包工具」，作者為 Bilibili UP 主「风尘WD」。
2. 本軟件在設計、程式碼起草、檢查、除錯及文字整理過程中使用了 AI 工具輔助，並非全部由作者逐行人工編寫。AI 輔助可能產生遺漏或錯誤，請結合兼容性報告、遊戲記錄及實際啟動結果獨立判斷。
3. AI 工具及其服務提供者不是本軟件的作者、維護者或擔保方，亦不對本軟件的運行結果承擔責任。
4. 本軟件是圍繞遊戲《Minecraft》整合包處理而獨立開發的第三方輔助工具，不包含、取代或授權《Minecraft》遊戲本體，亦並非 Minecraft 官方產品；本軟件並非由 Mojang Studios 或 Microsoft 開發、批准、認可、贊助或背書，本軟件及作者與上述主體不存在隸屬、代理或合作關係。
5. 就本軟件目前的設計、預期用途及發佈方式而言，作者以遵守現行 Minecraft EULA 及 Usage Guidelines 為開發原則，不以修改、取代或未經授權發佈遊戲本體為目的。相關規則可能更新，應以官方現行文本為準；本條不構成對用戶任何具體使用、修改或發佈行為必然合規的保證。
   Minecraft EULA：https://www.minecraft.net/eula
   Minecraft Usage Guidelines：https://www.minecraft.net/usage-guidelines

二、許可協議（CC BY-NC-SA 4.0）

1. 本軟件由作者依據「共享創意 姓名標示—非商業性—相同方式分享 4.0 國際許可協議」（CC BY-NC-SA 4.0）免費許可及發佈。
2. 在遵守許可條件的前提下，你可以複製、分享及轉載本軟件，也可以修改、改編並基於本軟件創作。
3. 姓名標示（BY）：分享或修改時，應以合理方式標示軟件名稱及作者帳戶名「风尘WD」，提供本許可協議連結，保留已有版權及許可說明，並說明是否作出修改；不得暗示作者為你的版本、用途或行為背書。
4. 非商業性使用（NC）：不得將本軟件或其修改版本主要用於獲取商業利益或金錢報酬。商業授權需要另行取得作者明確許可。
5. 相同方式共享（SA）：公開發佈修改版本或演繹作品時，應繼續採用 CC BY-NC-SA 4.0 或該許可允許的兼容許可。
6. 不得附加法律條款、數碼版權管理措施或其他技術限制，以阻止接收者行使本許可已授予的權利。
7. 上述內容只屬主要條款摘要，不能取代許可協議法律文本。如摘要與正式文本不一致，以官方協議原文為準：
   https://creativecommons.org/licenses/by-nc-sa/4.0/

三、版權與第三方權利

1. AI 輔助本身不當然改變作者對其具有獨創性的人類創作、選擇、編排、修改及整合部分享有的版權及相關權利；具體權利範圍以適用法律認定為準。
2. 在法律允許的範圍內，作者保留對軟件功能說明、本協議未盡事項及後續版本的解釋和更新權。此約定不限制用戶依法享有的權利，亦不改變已經依據 CC BY-NC-SA 4.0 合法取得並按約行使的許可權利。
3. Minecraft、CurseForge、Modrinth、各載入器、模組、資源包、光影包、整合包內容、第三方程式庫、商標及服務分別歸其權利人所有，並適用各自的許可、用戶協議及規則。本軟件的許可不代表作者有權再次許可這些第三方內容。

四、使用條件與用戶責任

1. 你只應處理自己擁有或已獲授權使用、遷移及發佈的整合包和內容，並遵守適用法律、Minecraft EULA、平台規則及每個內容項目的許可條件。
2. 本軟件不會授予你繞過下載限制、存取控制、平台規則或第三方許可的權利。因產生、上載、分享、營運或商業使用新整合包而產生的合規責任，由實施相關行為的用戶承擔。
3. 遷移前應自行備份原整合包、設定、實例及世界存檔。不得將本軟件的靜態兼容性報告視為模組必定可以啟動、存檔必定安全或伺服器必定穩定的保證。

五、聯網、資料與本機檔案

1. 為搜尋項目、查詢版本、取得載入器資料及按需要下載檔案，本軟件會存取 CurseForge、Modrinth 及相關載入器或下載服務，並可能向這些服務傳送項目 ID、檔案雜湊、檔案名稱或搜尋關鍵字、目標遊戲版本及載入器等查詢資料。
2. 部分核心功能需要聯網。按下「我已閱讀並同意」即表示你已知悉並同意本軟件為實現上述功能發出必要的網絡請求，並同意相關第三方服務按其規則處理請求所需資料；如不願接受此類聯網操作，請不要同意並停止使用本軟件。
3. 網絡中斷、連線波動、DNS 或代理異常、防火牆或保安軟件攔截、平台介面調整、授權變更、流量限制、維護或故障，以及地區網絡可用性差異，均可能導致軟件部分或全部功能暫時或持續無法使用、請求逾時、查詢或下載失敗、結果不完整。作者不保證相關網絡服務持續、及時或無錯誤可用；如介意此類風險，請勿使用本軟件。
4. 本軟件不會主動把你選擇的整合包封存檔本體上載給作者。第三方服務仍可能按照其私隱政策及伺服器記錄規則處理你的網絡位址、請求內容及其他必要連線資料。
5. 本軟件會在本機建立設定記錄及臨時解壓檔案，並在正常退出時嘗試清理臨時內容。首次同意狀態只儲存在本機設定中；刪除該設定後，軟件會再次顯示本協議。

六、功能邊界、免責聲明與責任限制

1. 本軟件仍在持續開發。平台資料可能缺失、過時或錯誤；網絡、API、下載權限、檔案雜湊、模組執行時行為及遊戲版本差異均可能導致遺漏、誤判、下載失敗、啟動崩潰、內容遺失或存檔損壞。
2. 兼容性檢查主要基於整合包清單及平台可用資料，不執行 Minecraft 或模組程式碼，無法窮盡依賴版本範圍、Mixin、註冊表、資料包、設定、世界存檔及只在執行時出現的問題。
3. 在適用法律允許的最大範圍內，本軟件按「現狀」及「可用狀態」提供，不就適銷性、特定用途適用性、無錯誤或不侵權作出任何明示或默示保證。對於使用或無法使用本軟件造成的間接損失、資料遺失或業務中斷，作者只在法律規定的範圍內承擔責任；法律不得排除或限制的責任不受本條影響。

七、協議更新與其他

1. 作者可因應軟件功能、許可說明或合規要求更新本協議。協議有實質更新時，後續版本可要求你重新閱讀並同意；新協議不追溯剝奪已經依法取得的許可權利。
2. 本協議任何條款被認定無效或不可執行時，不影響其他條款的效力。
3. 按下同意只表示你接受目前顯示版本的協議。你可以選擇不同意並退出軟件。
""";

    private const string English = """
MC Modpack Tool User Agreement and Important Information

Effective date: August 5, 2026

Read and understand this agreement in full before using the application. Clicking "I Have Read and Agree" means that you agree to be bound by it. If you do not agree, exit and stop using the application.

1. Application and Author

1. The application is named "MC Modpack Tool" (Chinese name: "MC整合包工具") and is authored by Bilibili creator FengchenWD (风尘WD).
2. AI tools assisted with design, code drafting, review, debugging, and writing. The application was not written entirely by the author line by line. AI assistance may introduce omissions or errors, so use the compatibility report, game logs, and actual launch results to make your own assessment.
3. The providers of the AI tools and services are not authors, maintainers, or guarantors of this application and are not responsible for its operation.
4. This is an independently developed third-party utility for processing Minecraft modpacks. It does not include, replace, or license the Minecraft game and is not an official Minecraft product. It is not developed, approved, endorsed, sponsored, or supported by Mojang Studios or Microsoft, and neither the application nor its author has an affiliation, agency, or partnership relationship with those entities.
5. The application's current design, intended use, and distribution model are developed with the current Minecraft EULA and Usage Guidelines in mind. It is not intended to modify, replace, or distribute the game itself without authorization. Those rules may change and the current official text controls. This paragraph does not guarantee that any particular use, modification, or distribution by a user will comply with those rules.
   Minecraft EULA: https://www.minecraft.net/eula
   Minecraft Usage Guidelines: https://www.minecraft.net/usage-guidelines

2. License (CC BY-NC-SA 4.0)

1. The author licenses and distributes this application free of charge under the Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International license (CC BY-NC-SA 4.0).
2. Subject to the license terms, you may copy, share, and redistribute the application, and may remix, transform, and build upon it.
3. Attribution (BY): When sharing or modifying the application, give appropriate credit to the application and its author, FengchenWD, provide a link to the license, retain existing copyright and license notices, and indicate whether changes were made. You may not imply that the author endorses your version, use, or conduct.
4. NonCommercial (NC): You may not use the application or a modified version primarily for commercial advantage or monetary compensation. Commercial licensing requires separate, express permission from the author.
5. ShareAlike (SA): If you publicly distribute a modified version or derivative work, you must use CC BY-NC-SA 4.0 or a compatible license permitted by it.
6. You may not impose additional legal terms, digital rights management, or technological restrictions that prevent recipients from exercising rights granted by the license.
7. The text above summarizes major terms and does not replace the legal code. If the summary conflicts with the official license, the official license controls:
   https://creativecommons.org/licenses/by-nc-sa/4.0/

3. Copyright and Third-Party Rights

1. AI assistance does not by itself alter copyright or related rights in the author's original human-created expression, selection, arrangement, modification, and integration. The exact scope of rights is determined under applicable law.
2. To the extent permitted by law, the author retains the right to explain and update application functionality, matters not covered by this agreement, and later releases. This provision does not restrict rights granted to users by law and does not alter license rights lawfully obtained and exercised under CC BY-NC-SA 4.0.
3. Minecraft, CurseForge, Modrinth, loaders, mods, resource packs, shader packs, modpack content, third-party libraries, trademarks, and services belong to their respective rights holders and are governed by their own licenses, agreements, and rules. This application's license does not mean that the author can relicense third-party content.

4. Conditions of Use and User Responsibility

1. Process only modpacks and content that you own or are authorized to use, migrate, and distribute. Follow applicable law, the Minecraft EULA, platform rules, and the license terms of each content item.
2. This application does not grant permission to bypass download restrictions, access controls, platform rules, or third-party licenses. The user who creates, uploads, shares, operates, or commercially uses a new modpack is responsible for compliance arising from those actions.
3. Back up the source modpack, configurations, instances, and worlds before migration. Do not treat the static compatibility report as a guarantee that mods will launch, worlds will remain safe, or servers will remain stable.

5. Network Access, Data, and Local Files

1. To search for projects, query versions, retrieve loader information, and download requested files, the application connects to CurseForge, Modrinth, and related loader or download services. Queries may include project IDs, file hashes, filenames or search terms, target game versions, and loader information.
2. Some core features require network access. Clicking "I Have Read and Agree" means that you understand and consent to the network requests required for those features and to the relevant third-party services processing the information required by each request under their own rules. If you do not accept this network activity, do not agree and stop using the application.
3. Network outages or instability, DNS or proxy issues, firewall or security software, API changes, authorization changes, rate limits, maintenance or outages, and regional differences in network availability may cause some or all features to be temporarily or persistently unavailable, requests to time out, searches or downloads to fail, or results to be incomplete. The author does not guarantee continuous, timely, or error-free availability of third-party network services. Do not use the application if you do not accept these risks.
4. As part of its normal operation, the application does not upload the selected modpack archive itself to the author. Third-party services may still process your network address, request data, and other connection information under their privacy policies and server logging practices.
5. The application creates local configuration records and temporary extracted files and attempts to clean temporary content during a normal exit. Agreement acceptance is stored only in the local user configuration. Deleting that configuration causes the agreement to be shown again.

6. Functional Limits, Disclaimer, and Limitation of Liability

1. The application remains under development. Platform metadata may be missing, outdated, or incorrect. Network conditions, APIs, download permissions, file hashes, mod runtime behavior, and game-version differences may cause omissions, incorrect conclusions, failed downloads, launch crashes, content loss, or world corruption.
2. Compatibility checks are primarily based on modpack manifests and available platform metadata. They do not execute Minecraft or mod code and cannot exhaustively identify dependency-version ranges, Mixins, registries, data packs, configurations, worlds, or issues that occur only at runtime.
3. To the fullest extent permitted by applicable law, the application is provided "as is" and "as available," without express or implied warranties of merchantability, fitness for a particular purpose, freedom from errors, or non-infringement. The author is responsible for indirect loss, data loss, or business interruption caused by use or inability to use the application only to the extent required by law. Liability that cannot lawfully be excluded or limited is unaffected.

7. Updates and Other Terms

1. The author may update this agreement as application functionality, licensing information, or compliance requirements change. A later release may require renewed acceptance after a material update. A new agreement does not retroactively remove license rights already obtained under law.
2. If any provision is found invalid or unenforceable, the remaining provisions remain effective.
3. Clicking agree means only that you accept the version currently displayed. You may decline and exit the application.
""";
}
