import unittest
from pathlib import Path
from structural_checks import code_only, direct_db_writes, double_members, empty_handlers, screen_hex_problems


class StructuralGuardTests(unittest.TestCase):
    def test_source_updater_is_version_scoped_and_non_destructive(self):
        root = Path(__file__).resolve().parents[2]
        updater = (root / 'UpdateProject.vbs').read_text(encoding='utf-16')
        self.assertIn('Const RELEASE_VERSION = "1.50.74"', updater)
        self.assertIn('HasReleaseVersion(targetProject, RELEASE_VERSION)', updater)
        self.assertIn('Stable 1.50.73 and all other versions are protected.', updater)
        self.assertNotIn('RemoveLegacyFiles', updater)
        self.assertNotIn('RemoveLegacyFolders', updater)
        self.assertNotIn('DeleteFile', updater)
        self.assertNotIn('DeleteFolder', updater)

    def test_doc_comment_is_not_an_empty_button(self):
        self.assertEqual([], empty_handlers('/// Old code: WithPrint((_, _) => { })\n'))

    def test_block_comment_and_literal_are_not_buttons(self):
        self.assertEqual([], empty_handlers('/* WithSave((_, _) => {}) */\nvar example = "WithPrint((_, _) => {})";'))

    def test_real_handler_is_detected_including_multiline_and_comment_body(self):
        self.assertEqual([2], empty_handlers('var x = toolbar\n.WithSave(\n (_, _) => { /* TODO */\n });'))

    def test_working_handler_is_not_rejected(self):
        self.assertEqual([], empty_handlers('toolbar.WithPrint((_, _) => Print());'))

    def test_literals_with_comment_markers_preserve_following_code(self):
        self.assertIn('var b', code_only('var s = "https://example.test"; var b = 1;'))

    def test_double_members_identify_class_and_computed_values(self):
        text = 'class Lot { public double Qty { get; set; } public double? Duration {get;set;} public double Free => Qty; }'
        self.assertEqual({'R.cs:Lot.Qty', 'R.cs:Lot.Duration', 'R.cs:Lot.Free'}, double_members('R.cs', text))

    def test_equal_count_cannot_hide_a_new_member(self):
        old = double_members('R.cs', 'class Lot { public double Qty {get;set;} }')
        new = double_members('R.cs', 'class Lot { public double Price {get;set;} }')
        self.assertEqual({'R.cs:Lot.Price'}, new - old)

    def test_direct_save_changes_in_ui_is_detected(self):
        text = 'var db = Get();\ndb.SaveChanges();\n'
        self.assertEqual([2], direct_db_writes(Path('SomeView.cs'), text))

    def test_save_changes_in_string_or_comment_is_not_a_write(self):
        text = '// db.SaveChanges();\nvar s = "SaveChanges()";\n'
        self.assertEqual([], direct_db_writes(Path('SomeView.cs'), text))

    def test_bootstrapper_is_allowed_to_save(self):
        self.assertEqual([], direct_db_writes(Path('Bootstrapper.cs'), 'db.SaveChanges();'))

    def test_new_screen_without_inline_hex_colors_is_allowed(self):
        self.assertEqual([], screen_hex_problems({'ThemeSettingsView.xaml': 0}, {}))

    def test_new_screen_with_inline_hex_color_is_rejected(self):
        problems = screen_hex_problems({'NewScreen.xaml': 1}, {})
        self.assertEqual(1, len(problems))
        self.assertIn('شاشة جديدة بألوان مضمّنة', problems[0])

    def test_existing_screen_hex_growth_is_rejected(self):
        problems = screen_hex_problems({'OrdersView.xaml': 33}, {'OrdersView.xaml': 32})
        self.assertEqual(1, len(problems))
        self.assertIn('الألوان المضمّنة زادت 32 ← 33', problems[0])


if __name__ == '__main__':
    unittest.main()
