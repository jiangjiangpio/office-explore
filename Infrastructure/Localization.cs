using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace OfficeTouchBar.Infrastructure;

internal static class Localization
{
    internal static readonly string[] Codes = ["zh-CN", "en", "fr", "ru", "es", "zh-TW", "pt", "lzh"];
    internal static readonly string[] Names = ["简体中文", "English", "Français", "Русский", "Español", "繁體中文", "Português", "文言文"];
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, string>> Originals = new();
    // Each row uses the same language order as Codes. Source text also serves as a stable key.
    private static readonly Dictionary<string, string[]> Texts = """
        标记故障并导出诊断包|Mark issue and export diagnostics|Marquer le problème et exporter le diagnostic|Отметить сбой и экспортировать диагностику|Marcar fallo y exportar diagnóstico|標記故障並匯出診斷包|Marcar falha e exportar diagnóstico|記故障並出診斷
        打开日志与诊断文件夹|Open logs and diagnostics folder|Ouvrir le dossier des journaux et diagnostics|Открыть папку журналов и диагностики|Abrir carpeta de registros y diagnóstico|開啟日誌與診斷資料夾|Abrir pasta de registos e diagnóstico|開日誌診斷之夾
        登录自启动时切换为复制显示|Duplicate displays on Windows login|Dupliquer les écrans à la connexion|Дублировать экраны при входе|Duplicar pantallas al iniciar sesión|登入自啟動時切換為同步顯示|Duplicar ecrãs ao iniciar sessão|登入自啟則同屏
        立即切换为复制模式|Duplicate displays now|Dupliquer maintenant|Дублировать сейчас|Duplicar ahora|立即切換為同步顯示|Duplicar agora|即刻同屏
        清理显示切换进程|Clean up display switch processes|Nettoyer les processus d’affichage|Очистить процессы переключения|Limpiar procesos de pantalla|清理顯示切換程序|Limpar processos de ecrã|清理切屏程序
        仅清理本应用启动的显示切换进程；不会关闭 PowerPoint。|Only display switch processes started by this app are cleaned up. PowerPoint stays open.|Seuls les processus d’affichage lancés par cette application sont nettoyés. PowerPoint reste ouvert.|Очистка только процессов, запущенных этим приложением. PowerPoint не закрывается.|Solo se limpian los procesos iniciados por esta aplicación. PowerPoint permanece abierto.|僅清理本應用啟動的顯示切換程序；不會關閉 PowerPoint。|Só limpa processos iniciados por esta aplicação. O PowerPoint permanece aberto.|惟清本程所啟切屏程序，不閉 PowerPoint。
        自动删除前一天及更早的内置笔迹|Delete built-in ink from previous days|Supprimer l’encre intégrée des jours précédents|Удалять пометки за предыдущие дни|Eliminar tinta integrada de días anteriores|自動刪除前一天及更早的內建筆跡|Eliminar tinta integrada de dias anteriores|自刪昨日及此前內置筆跡
        按本地日期清理；程序未运行时，下次启动补清理。不影响 PPT 内的 Office 原生墨迹。|Uses local dates; catches up on next launch. Office ink inside PPT files is unaffected.|Utilise la date locale ; rattrapage au prochain lancement. L’encre Office du PPT est conservée.|По местной дате; пропуски очищаются при запуске. Пометки Office внутри PPT сохраняются.|Usa fechas locales; completa la limpieza al iniciar. No afecta a la tinta Office del PPT.|按本地日期清理；未執行時下次啟動補清理。不影響 PPT 內的 Office 原生筆跡。|Usa datas locais; completa a limpeza ao iniciar. Não afeta a tinta Office no PPT.|依本地日期清理；未啟則下次補行。不動 PPT 原墨。
        已开启按日清理：保留的墨迹将在次日清除；程序关闭时顺延至下次启动。|Daily cleanup is enabled: kept ink expires the next day, or on next launch if the app is closed.|Nettoyage quotidien actif : l’encre conservée expire le lendemain ou au prochain lancement.|Ежедневная очистка включена: пометки удаляются на следующий день или при следующем запуске.|Limpieza diaria activada: la tinta conservada caduca al día siguiente o al volver a iniciar.|已開啟按日清理：保留的筆跡將於次日清除；關閉時順延至下次啟動。|Limpeza diária ativa: a tinta guardada expira no dia seguinte ou no próximo arranque.|日清已啟：留墨翌日清除；未啟則下次補行。
        常规|General|Général|Общие|General|一般|Geral|通設
        工具|Tools|Outils|Инструменты|Herramientas|工具|Ferramentas|器具
        内置书写时将鼠标限制在放映区域|Keep mouse within the slide while inking|Limiter la souris au diaporama pendant l'écriture|Ограничивать мышь областью показа при рисовании|Limitar el ratón a la presentación al dibujar|內建書寫時將滑鼠限制在放映區域|Limitar o rato à apresentação ao escrever|書寫時限游標於映域
        内置全屏书写时防止触屏边缘误触|Prevent touch edge gestures while inking full screen|Éviter les gestes de bord en écriture plein écran|Подавлять краевые жесты при полноэкранном рисовании|Evitar gestos de borde al dibujar a pantalla completa|內建全螢幕書寫時防止觸控邊緣誤觸|Evitar gestos de margem ao escrever em ecrã inteiro|全屏書寫時防屏緣誤觸
        切回鼠标或离开放映后恢复；边缘手势保护需在触控设备上验证。|Restored in mouse mode or when leaving the show. Test edge protection on your touchscreen.|Rétabli en mode souris ou hors diaporama. Vérifiez la protection sur votre écran tactile.|Восстанавливается в режиме мыши или вне показа. Проверьте защиту на сенсорном экране.|Se restaura en modo ratón o al salir. Compruebe la protección en su pantalla táctil.|切回滑鼠或離開放映後恢復；邊緣手勢保護需在觸控裝置上驗證。|Repõe no modo rato ou ao sair. Teste a proteção no seu ecrã tátil.|復游標或離映則還原；屏緣之護須實測。
        笔刷大小|Pen size|Taille du stylet|Размер пера|Tamaño del lápiz|筆刷大小|Tamanho da caneta|筆毫之粗
        橡皮擦大小|Eraser size|Taille de la gomme|Размер ластика|Tamaño del borrador|橡皮擦大小|Tamanho da borracha|橡皮之廣
        外观与位置|Appearance & position|Apparence et position|Вид и положение|Aspecto y posición|外觀與位置|Aspeto e posição|形貌與位
        打开或切换文稿时恢复鼠标|Reset to mouse on opening or switching presentations|Revenir au pointeur à l'ouverture ou au changement de présentation|Возвращать указатель при открытии или смене презентации|Restablecer el puntero al abrir o cambiar de presentación|開啟或切換簡報時恢復滑鼠|Repor o ponteiro ao abrir ou mudar de apresentação|啟稿易稿，復為游標
        自动收起墨迹工具面板|Automatically close ink tools|Fermer automatiquement les outils d'encre|Автоматически скрывать инструменты рисования|Cerrar automáticamente las herramientas de tinta|自動收起筆跡工具面板|Fechar automaticamente as ferramentas de tinta|自收筆具
        显示页码|Show slide number|Afficher le numéro de diapositive|Показывать номер слайда|Mostrar número de diapositiva|顯示頁碼|Mostrar número do diapositivo|顯頁碼
        锁定侧栏位置|Lock sidebar position|Verrouiller la position|Зафиксировать положение панели|Bloquear posición de la barra|鎖定側欄位置|Bloquear posição da barra|定側欄之位
        侧栏整体透明度|Overall sidebar transparency|Transparence de toute la barre|Прозрачность всей панели|Transparencia de toda la barra|側欄整體透明度|Transparência de toda a barra|側欄全體透度
        悬停或触控时提高可见度|Increase visibility on hover or touch|Renforcer la visibilité au survol ou au toucher|Повышать видимость при наведении или касании|Aumentar visibilidad al pasar el ratón o tocar|懸停或觸控時提高可見度|Aumentar visibilidade ao passar o rato ou tocar|懸停觸之則明
        交互时透明度|Interaction transparency|Transparence en interaction|Прозрачность при взаимодействии|Transparencia al interactuar|互動時透明度|Transparência ao interagir|觸時透度
        边缘距离|Edge distance|Distance du bord|Отступ от края|Distancia al borde|邊緣距離|Distância à margem|距邊
        垂直位置|Vertical position|Position verticale|Положение по вертикали|Posición vertical|垂直位置|Posição vertical|上下之位
        关闭|Close|Fermer|Закрыть|Cerrar|關閉|Fechar|閉
        是否保留内置墨迹批注？|Keep built-in ink annotations?|Conserver les annotations à l'encre intégrée ?|Сохранить пометки встроенного пера?|¿Conservar las anotaciones de tinta integrada?|是否保留內建筆跡註解？|Guardar as anotações de tinta integrada?|內置墨註，留乎？
        墨迹单独保存，不写入 PPT。放弃将删除此文稿全部已存内置墨迹。|Ink is saved separately, not in the PPT. Discard deletes all saved built-in ink for this presentation.|L'encre est enregistrée séparément, hors du PPT. Supprimer efface toute l'encre intégrée enregistrée pour cette présentation.|Пометки сохраняются отдельно от PPT. Удаление сотрёт все сохранённые пометки встроенного пера для этой презентации.|La tinta se guarda aparte del PPT. Descartar elimina toda la tinta integrada guardada de esta presentación.|筆跡獨立儲存，不寫入 PPT。捨棄將刪除此簡報全部已存內建筆跡。|A tinta é guardada fora do PPT. Descartar apaga toda a tinta integrada guardada desta apresentação.|墨另存，不入 PPT。棄則盡刪此稿所存內置筆墨。
        OfficeTouchBar 设置|OfficeTouchBar Settings|Paramètres OfficeTouchBar|Настройки OfficeTouchBar|Ajustes de OfficeTouchBar|OfficeTouchBar 設定|Definições do OfficeTouchBar|OfficeTouchBar 設
        PowerPoint 触控翻页侧栏|PowerPoint touch sidebar|Barre tactile PowerPoint|Сенсорная панель PowerPoint|Barra táctil de PowerPoint|PowerPoint 觸控翻頁側欄|Barra tátil do PowerPoint|PowerPoint 觸控側欄
        界面语言|Language|Langue|Язык|Idioma|介面語言|Idioma|語言
        开机自启动|Start with Windows|Démarrer avec Windows|Запускать с Windows|Iniciar con Windows|開機自動啟動|Iniciar com o Windows|開機即啟
        启动后自动启用侧栏检测|Enable sidebar detection on startup|Activer la détection au démarrage|Включать обнаружение при запуске|Detectar al iniciar|啟動後自動啟用側欄偵測|Ativar deteção ao iniciar|啟動即察側欄
        PowerPoint 激活时显示侧栏|Show when PowerPoint is active|Afficher avec PowerPoint actif|Показывать при активном PowerPoint|Mostrar con PowerPoint activo|PowerPoint 啟用時顯示側欄|Mostrar com PowerPoint ativo|PowerPoint 用時顯欄
        仅在放映模式显示|Show only during slide shows|Afficher seulement en diaporama|Показывать только при показе слайдов|Mostrar solo durante la presentación|僅在放映模式顯示|Mostrar apenas na apresentação|惟放映時顯
        跟随 PowerPoint 窗口|Follow PowerPoint window|Suivre la fenêtre PowerPoint|Следовать за окном PowerPoint|Seguir la ventana de PowerPoint|跟隨 PowerPoint 視窗|Seguir a janela do PowerPoint|隨 PowerPoint 窗
        靠近屏幕边缘时自动吸附|Snap to screen edges|Aligner sur les bords de l'écran|Прикреплять к краям экрана|Ajustar a los bordes de la pantalla|靠近螢幕邊緣時自動吸附|Ajustar às margens do ecrã|近屏緣則附
        显示退出幻灯片按钮|Show exit slide show button|Afficher le bouton de fin du diaporama|Показывать кнопку завершения показа|Mostrar botón de salir|顯示退出放映按鈕|Mostrar botão de terminar|顯止映鈕
        墨迹模式|Ink mode|Mode d'encre|Режим рукописного ввода|Modo de tinta|筆跡模式|Modo de tinta|筆墨之式
        Office 原生笔|Office pen|Stylet Office|Перо Office|Lápiz de Office|Office 原生筆|Caneta do Office|Office 原筆
        内置笔（自动逐页保存）|Built-in pen (autosave)|Stylet intégré (sauvegarde auto)|Встроенное перо (автосохранение)|Lápiz integrado (guardado automático)|內建筆（逐頁自動儲存）|Caneta integrada (gravação automática)|內置筆（逐頁自存）
        停靠位置|Dock position|Position|Положение панели|Posición|停靠位置|Posição|停駐之所
        左侧|Left|Gauche|Слева|Izquierda|左側|Esquerda|左
        右侧|Right|Droite|Справа|Derecha|右側|Direita|右
        侧栏透明度|Sidebar transparency|Transparence|Прозрачность панели|Transparencia|側欄透明度|Transparência|側欄透度
        按钮大小|Button size|Taille des boutons|Размер кнопок|Tamaño de los botones|按鈕大小|Tamanho dos botões|鈕之大小
        取消|Cancel|Annuler|Отмена|Cancelar|取消|Cancelar|罷
        保存|Save|Enregistrer|Сохранить|Guardar|儲存|Guardar|存
        保存设置失败|Could not save settings|Échec de l'enregistrement|Не удалось сохранить настройки|No se pudieron guardar los ajustes|儲存設定失敗|Não foi possível guardar|存設未成
        关闭墨迹工具|Close ink tools|Fermer les outils d'encre|Закрыть инструменты рисования|Cerrar herramientas de tinta|關閉筆跡工具|Fechar ferramentas de tinta|收筆具
        鼠标光标|Mouse pointer|Pointeur|Указатель мыши|Puntero del ratón|滑鼠游標|Ponteiro do rato|游標
        普通笔|Pen|Stylet|Перо|Lápiz|一般筆|Caneta|筆
        荧光笔|Highlighter|Surligneur|Маркер|Resaltador|螢光筆|Marcador|熒光筆
        橡皮擦|Eraser|Gomme|Ластик|Borrador|橡皮擦|Borracha|橡皮
        黑色|Black|Noir|Чёрный|Negro|黑色|Preto|黑
        深灰色|Dark gray|Gris foncé|Тёмно-серый|Gris oscuro|深灰色|Cinzento escuro|深灰
        红色|Red|Rouge|Красный|Rojo|紅色|Vermelho|赤
        橙红色|Red-orange|Rouge orangé|Красно-оранжевый|Rojo anaranjado|橙紅色|Vermelho-alaranjado|橙赤
        橙黄色|Amber|Ambre|Янтарный|Ámbar|橙黃色|Âmbar|橙黃
        黄色|Yellow|Jaune|Жёлтый|Amarillo|黃色|Amarelo|黃
        浅绿色|Light green|Vert clair|Светло-зелёный|Verde claro|淺綠色|Verde claro|淺綠
        绿色|Green|Vert|Зелёный|Verde|綠色|Verde|綠
        青色|Cyan|Cyan|Бирюзовый|Cian|青色|Ciano|青
        蓝色|Blue|Bleu|Синий|Azul|藍色|Azul|藍
        深蓝色|Dark blue|Bleu foncé|Тёмно-синий|Azul oscuro|深藍色|Azul escuro|深藍
        紫色|Purple|Violet|Фиолетовый|Morado|紫色|Roxo|紫
        擦除当前页全部墨迹|Erase all ink on this slide|Effacer l'encre de cette diapositive|Стереть все пометки на слайде|Borrar toda la tinta de esta diapositiva|清除本頁全部筆跡|Apagar toda a tinta deste diapositivo|盡去此頁墨
        上一项 / 上一张幻灯片|Previous step / slide|Étape / diapositive précédente|Предыдущий шаг / слайд|Paso / diapositiva anterior|上一項 / 上一張投影片|Passo / diapositivo anterior|前項／前頁
        下一项 / 下一张幻灯片|Next step / slide|Étape / diapositive suivante|Следующий шаг / слайд|Paso / diapositiva siguiente|下一項 / 下一張投影片|Passo / diapositivo seguinte|後項／後頁
        展开墨迹工具|Open ink tools|Ouvrir les outils d'encre|Открыть инструменты рисования|Abrir herramientas de tinta|展開筆跡工具|Abrir ferramentas de tinta|展筆具
        当前幻灯片 / 总幻灯片数|Current slide / total|Diapositive actuelle / total|Текущий слайд / всего|Diapositiva actual / total|目前投影片 / 總數|Diapositivo atual / total|此頁／總頁
        退出幻灯片放映|End slide show|Terminer le diaporama|Завершить показ|Terminar presentación|結束投影片放映|Terminar apresentação|止放映
        打开设置|Settings|Paramètres|Настройки|Ajustes|開啟設定|Definições|啟設
        暂停侧栏检测|Pause sidebar detection|Suspendre la détection|Приостановить обнаружение|Pausar detección|暫停側欄偵測|Pausar deteção|暫停偵察
        恢复侧栏检测|Resume sidebar detection|Reprendre la détection|Возобновить обнаружение|Reanudar detección|恢復側欄偵測|Retomar deteção|復偵察
        退出|Exit|Quitter|Выход|Salir|結束|Sair|退
        保留墨迹？|Keep ink?|Conserver l'encre ?|Сохранить пометки?|¿Conservar la tinta?|保留筆跡？|Guardar a tinta?|留墨乎？
        保留|Keep|Conserver|Сохранить|Conservar|保留|Guardar|留
        放弃|Discard|Supprimer|Удалить|Descartar|捨棄|Descartar|棄
        放映已结束。是否保留此文稿的内置墨迹？|The slide show has ended. Keep the built-in ink for this presentation?|Le diaporama est terminé. Conserver l'encre intégrée de cette présentation ?|Показ завершён. Сохранить пометки встроенного пера для этой презентации?|La presentación ha terminado. ¿Conservar la tinta integrada de este archivo?|放映已結束。是否保留此簡報的內建筆跡？|A apresentação terminou. Guardar a tinta integrada desta apresentação?|放映既畢，此稿之墨留乎？
        保留：存为本应用独立文件，不写入 PPT。放弃：删除此文稿所有已保存的内置墨迹。关闭此窗口默认保留。|Keep: save in separate app files, not in the PPT. Discard: delete all saved built-in ink for this presentation. Closing this window keeps the ink.|Conserver : fichiers séparés, sans modifier le PPT. Supprimer : effacer toute l'encre intégrée enregistrée pour cette présentation. Fermer cette fenêtre conserve l'encre.|Сохранить: в отдельных файлах, не в PPT. Удалить: стереть все сохранённые пометки встроенного пера для этой презентации. Закрытие окна сохраняет пометки.|Conservar: archivos separados, sin modificar el PPT. Descartar: eliminar toda la tinta integrada guardada de esta presentación. Cerrar esta ventana conserva la tinta.|保留：存為本應用程式獨立檔案，不寫入 PPT。捨棄：刪除此簡報所有已儲存的內建筆跡。關閉此視窗預設保留。|Guardar: ficheiros separados, sem alterar o PPT. Descartar: apagar toda a tinta integrada guardada desta apresentação. Fechar esta janela mantém a tinta.|留則別存於檔，不入 PPT；棄則盡刪此稿所存內置筆墨。閉窗則留。
        处理墨迹失败|Could not process ink|Échec du traitement de l'encre|Не удалось обработать пометки|No se pudo procesar la tinta|處理筆跡失敗|Não foi possível processar a tinta|處墨未成
        已在系统托盘后台运行。|Running in the system tray.|Actif dans la zone de notification.|Работает в области уведомлений.|En ejecución en la bandeja del sistema.|已在系統匣背景執行。|Em execução na área de notificação.|已駐系統托盤。
        已启动，侧栏检测已暂停。|Started; sidebar detection is paused.|Démarré ; détection suspendue.|Запущено; обнаружение приостановлено.|Iniciado; detección pausada.|已啟動，側欄偵測已暫停。|Iniciado; deteção pausada.|已啟，偵察暫止。
        当前 PowerPoint 状态不支持此操作，或放映窗口未能获得焦点。|This action is unavailable, or the slide show could not receive focus.|Action indisponible ou diaporama sans focus.|Действие недоступно или окно показа не получило фокус.|Acción no disponible o la presentación no obtuvo el foco.|目前 PowerPoint 狀態不支援此操作，或放映視窗未取得焦點。|Ação indisponível ou a apresentação não recebeu o foco.|此時未能行之，或放映未得焦點。
        """.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim().Split('|')).ToDictionary(row => row[0]);

    internal static string Get(string text, string language)
    {
        var index = Array.IndexOf(Codes, language);
        return Texts.TryGetValue(text, out var row) ? row[Math.Max(0, index)] : text;
    }

    internal static bool HasCompleteTranslations => Texts.Values.All(row => row.Length == Codes.Length
        && row.All(value => !string.IsNullOrWhiteSpace(value)));

    internal static void Apply(DependencyObject root, string language)
    {
        Translate(root, FrameworkElement.ToolTipProperty, language);
        if (root is Window) Translate(root, Window.TitleProperty, language);
        if (root is TextBlock) Translate(root, TextBlock.TextProperty, language);
        if (root is ContentControl) Translate(root, ContentControl.ContentProperty, language);
        if (root is HeaderedContentControl) Translate(root, HeaderedContentControl.HeaderProperty, language);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            Apply(child, language);
    }

    private static void Translate(DependencyObject target, DependencyProperty property, string language)
    {
        var originals = Originals.GetOrCreateValue(target);
        if (!originals.TryGetValue(property, out var original))
        {
            if (target.GetValue(property) is not string text || !Texts.ContainsKey(text)) return;
            originals[property] = original = text;
        }
        target.SetValue(property, Get(original, language));
    }
}
