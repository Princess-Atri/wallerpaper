from pathlib import Path

p = Path(r"D:\Desktop\wallerpaper c\samples\WallerpaperShell\MainWindow.xaml")
text = p.read_text(encoding="utf-8-sig")
start = text.index("                    <!-- Properties:")
end = text.index("                    </Border>\n                </Grid>", start)
end += len("                    </Border>\n")
text = text[:start] + text[end:]

detail = '''
                <!-- Wallpaper detail page -->
                <Grid x:Name="DetailPage" Visibility="Collapsed" Padding="24,18,20,20"
                      Background="{ThemeResource ApplicationPageBackgroundThemeBrush}">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <StackPanel Orientation="Horizontal" Spacing="12" VerticalAlignment="Center">
                        <Button Content="&#xE72B;" FontFamily="Segoe MDL2 Assets"
                                Click="DetailBack_Click"
                                AutomationProperties.Name="返回壁纸列表"
                                ToolTipService.ToolTip="返回壁纸列表"/>
                        <TextBlock Text="壁纸详情" FontSize="26" FontWeight="SemiBold"/>
                    </StackPanel>

                    <Grid Grid.Row="1" Margin="0,16,0,0" ColumnSpacing="18">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="360"/>
                        </Grid.ColumnDefinitions>

                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="*"/>
                                <RowDefinition Height="Auto"/>
                            </Grid.RowDefinitions>
                            <Border CornerRadius="12" Background="#10151F"
                                    BorderThickness="1" BorderBrush="#2A3446">
                                <Grid>
                                    <Image x:Name="DetailPreviewImage" Stretch="Uniform"/>
                                    <MediaPlayerElement x:Name="DetailPlayer"
                                                        Visibility="Collapsed"
                                                        AreTransportControlsEnabled="False"
                                                        AutoPlay="False"
                                                        Stretch="Uniform"/>
                                </Grid>
                            </Border>
                            <StackPanel Grid.Row="1" Margin="4,12,4,0" Spacing="4">
                                <TextBlock x:Name="DetailTitleText" FontSize="22"
                                           FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                                <TextBlock x:Name="DetailKindText" FontSize="12"
                                           Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                                <TextBlock x:Name="DetailPathText" FontSize="11"
                                           TextWrapping="Wrap"
                                           Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                            </StackPanel>
                        </Grid>

                        <Border Grid.Column="1" Padding="20"
                                CornerRadius="12" Background="{ThemeResource LayerFillColorDefaultBrush}"
                                BorderThickness="1" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
                            <ScrollViewer>
                                <StackPanel Spacing="16">
                                    <TextBlock Text="属性" FontSize="20" FontWeight="SemiBold"/>
                                    <TextBlock x:Name="PropertyTitle" Text="未选择壁纸"
                                               FontWeight="SemiBold" TextWrapping="Wrap"
                                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>

                                    <StackPanel Orientation="Horizontal" Spacing="8">
                                        <Button x:Name="RenameButton" Content="重命名"
                                                Click="Rename_Click" IsEnabled="False"
                                                AutomationProperties.Name="重命名壁纸"/>
                                        <Button x:Name="DeleteButton" Content="从库中删除"
                                                Click="Delete_Click" IsEnabled="False"
                                                AutomationProperties.Name="删除壁纸"/>
                                    </StackPanel>

                                    <StackPanel Spacing="4">
                                        <Grid>
                                            <TextBlock Text="音量"/>
                                            <TextBlock x:Name="VolumeValue" Text="0" HorizontalAlignment="Right"
                                                       Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                                        </Grid>
                                        <Slider x:Name="VolumeSlider" Minimum="0" Maximum="100"
                                                AutomationProperties.Name="音量"
                                                AutomationProperties.HelpText="0 到 100"
                                                StepFrequency="1" ValueChanged="VolumeSlider_ValueChanged"/>
                                    </StackPanel>

                                    <StackPanel Spacing="4">
                                        <Grid>
                                            <TextBlock Text="播放速度"/>
                                            <TextBlock x:Name="SpeedValue" Text="1.0×" HorizontalAlignment="Right"
                                                       Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                                        </Grid>
                                        <Slider x:Name="SpeedSlider" Minimum="0.1" Maximum="4"
                                                AutomationProperties.Name="播放速度"
                                                StepFrequency="0.1" ValueChanged="SpeedSlider_ValueChanged"/>
                                    </StackPanel>

                                    <StackPanel Spacing="4">
                                        <Grid>
                                            <TextBlock Text="着色器强度"/>
                                            <TextBlock x:Name="ShaderValue" Text="1.00" HorizontalAlignment="Right"
                                                       Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                                        </Grid>
                                        <Slider x:Name="ShaderSlider" Minimum="0" Maximum="2"
                                                AutomationProperties.Name="着色器强度"
                                                StepFrequency="0.01" ValueChanged="ShaderSlider_ValueChanged"/>
                                    </StackPanel>

                                    <ToggleSwitch x:Name="MuteToggle" Header="静音"
                                                  AutomationProperties.Name="静音"
                                                  Toggled="MuteToggle_Toggled"/>

                                    <ComboBox x:Name="TargetMonitorCombo" Header="目标显示器"
                                              AutomationProperties.Name="目标显示器"
                                              HorizontalAlignment="Stretch"/>

                                    <Button x:Name="ApplyToMonitorButton" Content="应用到目标显示器"
                                            HorizontalAlignment="Stretch" Click="ApplyToMonitor_Click"
                                            IsEnabled="False"/>

                                    <Border Height="1" Background="{ThemeResource CardStrokeColorDefaultBrush}"/>

                                    <TextBlock Text="多显示器" FontWeight="SemiBold"/>
                                    <TextBlock Text="在“设置 → 多显示器”中为每块屏幕指定壁纸。"
                                               TextWrapping="Wrap" FontSize="12"
                                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>

                                    <Button Content="生成缩略图" Click="RebuildThumb_Click"
                                            HorizontalAlignment="Stretch"/>
                                </StackPanel>
                            </ScrollViewer>
                        </Border>
                    </Grid>
                </Grid>

'''

text = text.replace("                <!-- Settings -->", detail + "                <!-- Settings -->")
p.write_text(text, encoding="utf-8-sig")
print("updated")
