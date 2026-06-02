using UnityEngine;
using UnityEngine.UI;

namespace TapUITest
{
    /// <summary>
    /// tap_ui_element の動作確認用。Button の onClick で PanelA / PanelB を切り替える。
    /// 同じ Canvas 配下の "PanelA" / "PanelB" を Transform.Find で取得する（非アクティブも検索可能）。
    /// </summary>
    [RequireComponent(typeof(Button))]
    public sealed class PanelSwitcher : MonoBehaviour
    {
        private GameObject _panelA;
        private GameObject _panelB;
        private int _count;

        private void Start()
        {
            var canvas = GetComponentInParent<Canvas>().transform;
            _panelA = canvas.Find("PanelA").gameObject;
            _panelB = canvas.Find("PanelB").gameObject;
            GetComponent<Button>().onClick.AddListener(Switch);
            Debug.Log("[TapUITest] PanelSwitcher ready. Showing PanelA.");
        }

        private void Switch()
        {
            _count++;
            var showB = _panelA.activeSelf;
            _panelA.SetActive(!showB);
            _panelB.SetActive(showB);
            Debug.Log($"[TapUITest] Button tapped (count={_count}) -> showing {(showB ? "PanelB" : "PanelA")}");
        }
    }
}
